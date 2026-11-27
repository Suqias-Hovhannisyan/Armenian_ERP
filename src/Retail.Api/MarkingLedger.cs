using System.Data;
using Microsoft.Data.SqlClient;

namespace Retail.Api;

internal sealed record ScannedUnit(string Gtin, string Serial, string Payload);

internal static class MarkingLedger
{
    private const char GroupSeparator = (char)29;

    public static ScannedUnit[] Parse(string[]? inputs)
    {
        if (inputs is null || inputs.Length == 0)
            return [];

        var identities = new HashSet<(string, string)>();
        var units = new List<ScannedUnit>(inputs.Length);
        foreach (var input in inputs)
        {
            var payload = input ?? "";
            if (payload.StartsWith("]d2", StringComparison.Ordinal))
                payload = payload[3..];

            if (payload.Length is < 19 or > 512 || !payload.StartsWith("01", StringComparison.Ordinal) ||
                payload.AsSpan(2, 14).ContainsAnyExceptInRange('0', '9') || !payload.AsSpan(16, 2).SequenceEqual("21"))
                throw RetailException.Invalid("Scan a GS1 Data Matrix with AI 01 (GTIN) followed by AI 21 (serial).");

            var parts = payload[18..].Split(GroupSeparator);
            if (parts[0].Length is < 1 or > 20 ||
                parts.Any(part => part.Length == 0 || part.Any(c => c is < '!' or > '~')))
                throw RetailException.Invalid("Invalid serial or group separator in the scanned marking code.");

            var unit = new ScannedUnit(payload.Substring(2, 14), parts[0], payload);
            if (!identities.Add((unit.Gtin, unit.Serial)))
                throw RetailException.Invalid("A unit was scanned more than once in this document.");
            units.Add(unit);
        }

        return units.OrderBy(unit => unit.Payload, StringComparer.Ordinal).ToArray();
    }

    // Preserve fingerprints of existing unmarked transactions, including archived request IDs.
    public static object Fingerprint(object movement, ScannedUnit[] codes) => codes.Length == 0
        ? movement
        : new { Movement = movement, MarkingCodes = codes.Select(unit => unit.Payload) };

    public static async Task ApplyAsync(SqlConnection connection, SqlTransaction transaction, Guid productId,
        Guid movementId, Guid? saleId, string kind, decimal quantity, ScannedUnit[] units, CancellationToken token)
    {
        await using var settings = new SqlCommand(
            "SELECT RequiresMarking, Gtin FROM dbo.Products WITH (UPDLOCK) WHERE Id = @product;", connection, transaction);
        settings.Parameters.Add("@product", SqlDbType.UniqueIdentifier).Value = productId;
        bool required;
        string? gtin;
        await using (var reader = await settings.ExecuteReaderAsync(token))
        {
            if (!await reader.ReadAsync(token))
                throw RetailException.Missing("Product was not found.");
            required = reader.GetBoolean(0);
            gtin = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (!required)
        {
            if (units.Length != 0)
                throw RetailException.Invalid("This product is not configured for unit marking.");
            return;
        }

        if (quantity != units.Length || units.Any(unit => unit.Gtin != gtin))
            throw RetailException.Invalid("Scan one matching product marking code for each whole unit.");

        var sql = kind switch
        {
            "receipt" => """
                INSERT INTO dbo.MarkedUnits(Gtin, Serial, ProductId, RawCode)
                SELECT @gtin, @serial, @product, @payload
                WHERE NOT EXISTS (SELECT 1 FROM dbo.MarkedUnits WHERE Gtin = @gtin AND Serial = @serial);
                """,
            "sale" => """
                UPDATE dbo.MarkedUnits SET CurrentSaleId = @sale
                WHERE Gtin = @gtin AND Serial = @serial AND ProductId = @product
                AND RawCode = @payload AND CurrentSaleId IS NULL;
                """,
            "return" => """
                UPDATE dbo.MarkedUnits SET CurrentSaleId = NULL
                WHERE Gtin = @gtin AND Serial = @serial AND ProductId = @product
                AND RawCode = @payload AND CurrentSaleId = @sale;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        await using var command = new SqlCommand(sql + """

            DECLARE @changed int = @@ROWCOUNT;
            IF @changed = 1
                INSERT INTO dbo.MarkedUnitMovements (MovementId, Gtin, Serial) VALUES (@movement, @gtin, @serial);
            SELECT @changed;
            """, connection, transaction);
        command.Parameters.Add("@product", SqlDbType.UniqueIdentifier).Value = productId;
        command.Parameters.Add("@movement", SqlDbType.UniqueIdentifier).Value = movementId;
        command.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = (object?)saleId ?? DBNull.Value;
        command.Parameters.Add("@gtin", SqlDbType.VarChar, 14).Value = gtin!;
        var serial = command.Parameters.Add("@serial", SqlDbType.VarChar, 20);
        var payload = command.Parameters.Add("@payload", SqlDbType.VarChar, 512);
        foreach (var unit in units)
        {
            serial.Value = unit.Serial;
            payload.Value = unit.Payload;
            if ((int)(await command.ExecuteScalarAsync(token))! != 1)
                throw RetailException.Conflict("Unit already received, unavailable for sale, or not sold on this sale.");
        }
    }
}
