using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Retail.Api;

internal sealed class RetailStore(string connectionString)
{
    private const decimal MaximumQuantity = 1_000_000m;
    private const decimal MaximumStock = 1_000_000_000_000m;
    private const long MaximumPrice = 1_000_000_000;
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Product>> ListProductsAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT Id, Sku, Name, UnitPriceAmd, Stock FROM dbo.Products ORDER BY Sku;", connection);
        await using var reader = await command.ExecuteReaderAsync(token);

        var products = new List<Product>();
        while (await reader.ReadAsync(token))
        {
            products.Add(ReadProduct(reader));
        }

        return products;
    }

    public async Task<ProductStock> GetStockAsync(Guid productId, CancellationToken token)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        var product = await GetProductAsync(connection, null, productId, token);
        return new ProductStock(product.Id, product.Stock);
    }

    public Task<string> CreateProductAsync(CreateProductRequest request, CancellationToken token)
    {
        var sku = request.Sku?.Trim();
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(sku) || sku.Length > 64 || string.IsNullOrEmpty(name) || name.Length > 200)
        {
            throw RetailException.Invalid("Provide a SKU of 1–64 characters and a product name of 1–200 characters.");
        }

        if (request.UnitPriceAmd is < 1 or > MaximumPrice)
        {
            throw RetailException.Invalid("Unit price must be a positive whole AMD amount up to 1,000,000,000.");
        }

        return ExecuteWriteAsync(request.RequestId, new { Operation = "product", sku, name, request.UnitPriceAmd },
            async (connection, transaction) =>
            {
                var id = Guid.NewGuid();
                await using var command = new SqlCommand("""
                    INSERT INTO dbo.Products(Id, Sku, Name, UnitPriceAmd, Stock)
                    SELECT @id, @sku, @name, @price, 0
                    WHERE NOT EXISTS (SELECT 1 FROM dbo.Products WHERE Sku = @sku);
                    """, connection, transaction);
                AddId(command, "@id", id);
                command.Parameters.Add("@sku", SqlDbType.NVarChar, 64).Value = sku;
                command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = name;
                command.Parameters.Add("@price", SqlDbType.BigInt).Value = request.UnitPriceAmd;
                if (await command.ExecuteNonQueryAsync(token) == 0)
                {
                    throw RetailException.Conflict("The SKU is already in use.");
                }

                return new Product(id, sku, name, request.UnitPriceAmd, 0);
            }, token);
    }

    public Task<string> ReceiveAsync(StockRequest request, CancellationToken token)
    {
        ValidateQuantity(request.Quantity);
        return ExecuteWriteAsync(request.RequestId,
            new { Operation = "receipt", request.ProductId, Quantity = FormatQuantity(request.Quantity) },
            async (connection, transaction) =>
            {
                var product = await GetProductAsync(connection, transaction, request.ProductId, token);
                var movement = new StockMovement(Guid.NewGuid(), product.Id, request.Quantity, product.Stock + request.Quantity);
                await SetStockAsync(connection, transaction, movement.ProductId, movement.Stock, token);
                await AddMovementAsync(connection, transaction, movement.Id, product.Id, null, "receipt",
                    request.Quantity, null, null, token);
                return movement;
            }, token);
    }

    public Task<string> SellAsync(StockRequest request, CancellationToken token)
    {
        ValidateQuantity(request.Quantity);
        return ExecuteWriteAsync(request.RequestId,
            new { Operation = "sale", request.ProductId, Quantity = FormatQuantity(request.Quantity) },
            async (connection, transaction) =>
            {
                var product = await GetProductAsync(connection, transaction, request.ProductId, token);
                if (product.Stock < request.Quantity)
                {
                    throw RetailException.Conflict("Insufficient stock for this sale.");
                }

                var id = Guid.NewGuid();
                var movement = new SaleMovement(id, product.Id, request.Quantity, product.Stock - request.Quantity,
                    product.UnitPriceAmd, CalculateTotal(request.Quantity, product.UnitPriceAmd));
                await using var command = new SqlCommand("""
                    INSERT INTO dbo.Sales(Id, ProductId, Quantity, ReturnedQuantity, UnitPriceAmd)
                    VALUES (@id, @productId, @quantity, 0, @price);
                    """, connection, transaction);
                AddId(command, "@id", id);
                AddId(command, "@productId", product.Id);
                AddQuantity(command, "@quantity", request.Quantity);
                command.Parameters.Add("@price", SqlDbType.BigInt).Value = product.UnitPriceAmd;
                await command.ExecuteNonQueryAsync(token);

                await SetStockAsync(connection, transaction, product.Id, movement.Stock, token);
                await AddMovementAsync(connection, transaction, movement.Id, product.Id, id, "sale",
                    -request.Quantity, movement.UnitPriceAmd, movement.TotalAmd, token);
                return movement;
            }, token);
    }

    public Task<string> ReturnAsync(Guid saleId, ReturnRequest request, CancellationToken token)
    {
        ValidateQuantity(request.Quantity);
        return ExecuteWriteAsync(request.RequestId,
            new { Operation = "return", SaleId = saleId, Quantity = FormatQuantity(request.Quantity) },
            async (connection, transaction) =>
            {
                var sale = await GetSaleAsync(connection, transaction, saleId, token);
                var returnedQuantity = sale.ReturnedQuantity + request.Quantity;
                if (returnedQuantity > sale.Quantity)
                {
                    throw RetailException.Conflict("The requested quantity exceeds the remaining returnable quantity.");
                }

                var product = await GetProductAsync(connection, transaction, sale.ProductId, token);
                var totalAmd = CalculateTotal(returnedQuantity, sale.UnitPriceAmd)
                    - CalculateTotal(sale.ReturnedQuantity, sale.UnitPriceAmd);
                var movement = new SaleMovement(Guid.NewGuid(), product.Id, request.Quantity,
                    product.Stock + request.Quantity, sale.UnitPriceAmd, totalAmd);
                await using var command = new SqlCommand(
                    "UPDATE dbo.Sales SET ReturnedQuantity = @quantity WHERE Id = @id;", connection, transaction);
                AddQuantity(command, "@quantity", returnedQuantity);
                AddId(command, "@id", saleId);
                await command.ExecuteNonQueryAsync(token);

                await SetStockAsync(connection, transaction, product.Id, movement.Stock, token);
                await AddMovementAsync(connection, transaction, movement.Id, product.Id, saleId, "return",
                    request.Quantity, movement.UnitPriceAmd, movement.TotalAmd, token);
                return movement;
            }, token);
    }

    private async Task<string> ExecuteWriteAsync<T>(Guid requestId, object input,
        Func<SqlConnection, SqlTransaction, Task<T>> apply, CancellationToken token)
    {
        if (requestId == Guid.Empty)
        {
            throw RetailException.Invalid("A non-empty requestId is required.");
        }

        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        // Serialize this single-store pilot's writes across processes and connections.
        await using var lockCommand = new SqlCommand("""
            DECLARE @status int;
            EXEC @status = sys.sp_getapplock
                @Resource = N'ArmeniaRetailERP.RetailWrites.v1',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 10000;
            SELECT @status;
            """, connection, transaction);
        if (await lockCommand.ExecuteScalarAsync(token) is not int lockStatus || lockStatus < 0)
        {
            throw new RetailException(503, "The inventory writer is busy. Retry with the same requestId.");
        }

        var existing = await GetWriteRequestAsync(connection, transaction, requestId, token);
        if (existing is { } previous)
        {
            if (!CryptographicOperations.FixedTimeEquals(previous.Fingerprint, fingerprint))
            {
                throw RetailException.Conflict("This requestId was already used with different data.");
            }

            await transaction.CommitAsync(token);
            return previous.ResponseJson;
        }

        var response = await apply(connection, transaction);
        var json = JsonSerializer.Serialize(response, ResponseJson);
        await using var command = new SqlCommand("""
            INSERT INTO dbo.WriteRequests(RequestId, Fingerprint, ResponseJson)
            VALUES (@requestId, @fingerprint, @json);
            """, connection, transaction);
        AddId(command, "@requestId", requestId);
        command.Parameters.Add("@fingerprint", SqlDbType.Binary, 32).Value = fingerprint;
        command.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value = json;
        await command.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return json;
    }

    private static async Task<(byte[] Fingerprint, string ResponseJson)?> GetWriteRequestAsync(
        SqlConnection connection, SqlTransaction transaction, Guid requestId, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT Fingerprint, ResponseJson FROM dbo.WriteRequests WHERE RequestId = @requestId;",
            connection, transaction);
        AddId(command, "@requestId", requestId);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
        {
            return (reader.GetFieldValue<byte[]>(0), reader.GetString(1));
        }

        return null;
    }

    private static async Task<Product> GetProductAsync(SqlConnection connection, SqlTransaction? transaction,
        Guid id, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT Id, Sku, Name, UnitPriceAmd, Stock FROM dbo.Products WHERE Id = @id;", connection, transaction);
        AddId(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
        {
            return ReadProduct(reader);
        }

        throw RetailException.Missing("Product was not found.");
    }

    private static Product ReadProduct(SqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetDecimal(4));

    private static async Task<(Guid ProductId, decimal Quantity, decimal ReturnedQuantity, long UnitPriceAmd)> GetSaleAsync(
        SqlConnection connection, SqlTransaction transaction, Guid id, CancellationToken token)
    {
        await using var command = new SqlCommand("""
            SELECT ProductId, Quantity, ReturnedQuantity, UnitPriceAmd FROM dbo.Sales WHERE Id = @id;
            """, connection, transaction);
        AddId(command, "@id", id);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (await reader.ReadAsync(token))
        {
            return (reader.GetGuid(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetInt64(3));
        }

        throw RetailException.Missing("Sale was not found.");
    }

    private static async Task SetStockAsync(SqlConnection connection, SqlTransaction transaction,
        Guid productId, decimal stock, CancellationToken token)
    {
        if (stock > MaximumStock)
        {
            throw RetailException.Conflict("This change would exceed the maximum supported stock.");
        }

        await using var command = new SqlCommand(
            "UPDATE dbo.Products SET Stock = @stock WHERE Id = @id;", connection, transaction);
        AddId(command, "@id", productId);
        AddQuantity(command, "@stock", stock);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task AddMovementAsync(SqlConnection connection, SqlTransaction transaction,
        Guid id, Guid productId, Guid? saleId, string kind, decimal delta, long? price, long? total,
        CancellationToken token)
    {
        await using var command = new SqlCommand("""
            INSERT INTO dbo.StockMovements(Id, ProductId, SaleId, Kind, QuantityDelta, UnitPriceAmd, TotalAmd)
            VALUES (@id, @productId, @saleId, @kind, @delta, @price, @total);
            """, connection, transaction);
        AddId(command, "@id", id);
        AddId(command, "@productId", productId);
        AddId(command, "@saleId", saleId);
        command.Parameters.Add("@kind", SqlDbType.VarChar, 8).Value = kind;
        AddQuantity(command, "@delta", delta);
        command.Parameters.Add("@price", SqlDbType.BigInt).Value = (object?)price ?? DBNull.Value;
        command.Parameters.Add("@total", SqlDbType.BigInt).Value = (object?)total ?? DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }

    private static void ValidateQuantity(decimal quantity)
    {
        if (quantity <= 0 || quantity > MaximumQuantity || decimal.Round(quantity, 3) != quantity)
        {
            throw RetailException.Invalid("Quantity must be positive, at most 1,000,000, with up to three decimal places.");
        }
    }

    private static string FormatQuantity(decimal quantity) => quantity.ToString("0.###", CultureInfo.InvariantCulture);

    private static long CalculateTotal(decimal quantity, long unitPriceAmd) =>
        (long)decimal.Round(quantity * unitPriceAmd, 0, MidpointRounding.AwayFromZero);

    private static void AddId(SqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(name, SqlDbType.UniqueIdentifier).Value = (object?)value ?? DBNull.Value;

    private static void AddQuantity(SqlCommand command, string name, decimal value)
    {
        var parameter = command.Parameters.Add(name, SqlDbType.Decimal);
        parameter.Precision = 19;
        parameter.Scale = 3;
        parameter.Value = value;
    }
}
