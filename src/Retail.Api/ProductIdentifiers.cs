using System.Data;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Retail.Api;

internal sealed record IdentifierProfile(string? Barcode, string? Gtin, string? AtgCode,
    [property: JsonRequired] bool RequiresMarking);

internal sealed class ProductIdentifiers(string connectionString)
{
    public static void MapEndpoints(WebApplication app, string connectionString)
    {
        var service = new ProductIdentifiers(connectionString);
        app.MapGet("/api/products/{id:guid}/identifiers", service.GetAsync);
        app.MapGet("/api/products/by-barcode/{barcode}", service.LookupAsync);
        app.MapPut("/api/products/{id:guid}/identifiers", async (Guid id, IdentifierProfile profile,
            HttpContext context, CancellationToken token) =>
        {
            if (context.Items[typeof(RetailRole)] is not RetailRole.Admin)
                throw new RetailException(403, "Administrator access is required to update product identifiers.");
            await service.SaveAsync(id, profile, token);
            return Results.NoContent();
        }).WithMetadata(new RequireModelEditorToken());
    }

    private async Task<IdentifierProfile> GetAsync(Guid id, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var cmd = new SqlCommand("SELECT Barcode,Gtin,AtgCode,RequiresMarking FROM dbo.Products WHERE Id=@id", connection);
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        using var reader = await cmd.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw RetailException.Missing("Product not found.");
        return new(ReadOptional(reader, 0), ReadOptional(reader, 1), ReadOptional(reader, 2), reader.GetBoolean(3));
    }

    private async Task<Product> LookupAsync(string barcode, CancellationToken token)
    {
        var lookup = LookupKey(ValidateBarcode(barcode) ?? throw RetailException.Invalid("Barcode is required."));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var cmd = new SqlCommand("""
            SELECT Id,Sku,Name,UnitPriceAmd,Stock FROM dbo.Products
            WHERE BarcodeLookup=@lookup OR Gtin=@lookup
            """, connection);
        cmd.Parameters.Add("@lookup", SqlDbType.VarChar, 64).Value = lookup;
        using var reader = await cmd.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw RetailException.Missing("Barcode not found.");
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetDecimal(4));
    }

    private async Task SaveAsync(Guid id, IdentifierProfile input, CancellationToken token)
    {
        var barcode = ValidateBarcode(input.Barcode);
        var gtin = ValidateGtin(input.Gtin);
        var atg = Optional(input.AtgCode);
        if (atg is not null && (atg.Length != 10 || !atg.All(char.IsAsciiDigit)))
            throw RetailException.Invalid("Enter the ten digits of the commodity code.");
        if (input.RequiresMarking && gtin is null)
            throw RetailException.Invalid("A valid GTIN is required for marked products.");

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(token);
        using var cmd = new SqlCommand("""
            DECLARE @status int;
            EXEC @status = sys.sp_getapplock @Resource=N'ArmeniaRetailERP.RetailWrites.v1',
                @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
            SELECT @status;
            """, connection, tx);
        if (await cmd.ExecuteScalarAsync(token) is not int status || status < 0)
            throw new RetailException(503, "Inventory is busy. Retry the profile update.");

        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = id;
        cmd.CommandText = """
            SELECT Stock,RequiresMarking,Gtin,
                CASE WHEN EXISTS(SELECT 1 FROM dbo.MarkedUnits WHERE ProductId=@id) THEN 1 ELSE 0 END,
                CASE WHEN EXISTS(SELECT 1 FROM dbo.Sales WHERE ProductId=@id AND ReturnedQuantity < Quantity) THEN 1 ELSE 0 END
            FROM dbo.Products WITH (UPDLOCK) WHERE Id=@id;
            """;
        using (var row = await cmd.ExecuteReaderAsync(token))
        {
            if (!await row.ReadAsync(token)) throw RetailException.Missing("Product not found.");
            if (!row.GetBoolean(1) && input.RequiresMarking && row.GetDecimal(0) != 0)
                throw RetailException.Conflict("Enable marking only after reconciling existing stock to zero.");
            if (!row.GetBoolean(1) && input.RequiresMarking && row.GetInt32(4) == 1)
                throw RetailException.Conflict("Unmarked sales are still returnable. Use a new product for marked stock.");
            if (row.GetInt32(3) == 1 && (!input.RequiresMarking || gtin != ReadOptional(row, 2)))
                throw RetailException.Conflict("The marking mode and GTIN are fixed after units have been received.");
        }

        cmd.Parameters.Add("@barcode", SqlDbType.VarChar, 64).Value = (object?)barcode ?? DBNull.Value;
        cmd.Parameters.Add("@lookup", SqlDbType.VarChar, 64).Value = (object?)LookupKey(barcode) ?? DBNull.Value;
        cmd.Parameters.Add("@gtin", SqlDbType.VarChar, 14).Value = (object?)gtin ?? DBNull.Value;
        cmd.Parameters.Add("@atg", SqlDbType.VarChar, 10).Value = (object?)atg ?? DBNull.Value;
        cmd.Parameters.Add("@marked", SqlDbType.Bit).Value = input.RequiresMarking;
        cmd.CommandText = """
            SELECT 1 FROM dbo.Products WHERE Id<>@id AND
                (BarcodeLookup IN (@lookup,@gtin) OR Gtin IN (@lookup,@gtin));
            """;
        if (await cmd.ExecuteScalarAsync(token) is not null)
            throw RetailException.Conflict("Another product already uses this barcode or GTIN.");
        cmd.CommandText = """
            UPDATE dbo.Products SET Barcode=@barcode,Gtin=@gtin,AtgCode=@atg,RequiresMarking=@marked WHERE Id=@id;
            """;
        await cmd.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
    }

    private static string? ReadOptional(SqlDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.GetString(ordinal);

    private static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string? ValidateBarcode(string? text)
    {
        var barcode = Optional(text);
        if (barcode is not null && (barcode.Length > 64 ||
            !barcode.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw RetailException.Invalid("Use 1–64 barcode letters, digits, hyphens, underscores or periods.");
        return barcode;
    }

    private static string? LookupKey(string? barcode) =>
        barcode is { Length: 8 or 12 or 13 or 14 } && barcode.All(char.IsAsciiDigit)
            ? barcode.PadLeft(14, '0') : barcode;

    private static string? ValidateGtin(string? text)
    {
        var gtin = Optional(text);
        if (gtin is null) return null;
        if (gtin.Length is not (8 or 12 or 13 or 14) || !gtin.All(char.IsAsciiDigit))
            throw RetailException.Invalid("Use an 8-, 12-, 13- or 14-digit GTIN.");
        gtin = gtin.PadLeft(14, '0');
        int sum = 0;
        for (var i = 0; i < gtin.Length; i++)
            sum += (gtin[i] - '0') * (i % 2 == 0 ? 3 : 1);
        if (sum % 10 != 0 || gtin == "00000000000000")
            throw RetailException.Invalid("Invalid GTIN or check digit.");
        return gtin;
    }
}
