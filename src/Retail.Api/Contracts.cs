namespace Retail.Api;

public sealed record CreateProductRequest(Guid RequestId, string? Sku, string? Name, long UnitPriceAmd);
public sealed record StockRequest(Guid RequestId, Guid ProductId, decimal Quantity, string[]? MarkingCodes = null);
public sealed record CartRequest(Guid RequestId, CartLineRequest?[]? Lines);
public sealed record CartLineRequest(Guid ProductId, decimal Quantity, string[]? MarkingCodes = null);
public sealed record CartReceipt(Guid Id, long TotalAmd, IReadOnlyList<SaleMovement> Lines);
public sealed record ReturnRequest(Guid RequestId, decimal Quantity, string[]? MarkingCodes = null);
public sealed record Product(Guid Id, string Sku, string Name, long UnitPriceAmd, decimal Stock);
public sealed record ProductStock(Guid ProductId, decimal Quantity);
public sealed record StockMovement(Guid Id, Guid ProductId, decimal Quantity, decimal Stock);
public sealed record SaleMovement(Guid Id, Guid ProductId, decimal Quantity, decimal Stock, long UnitPriceAmd, long TotalAmd);

internal sealed class RetailException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public static RetailException Invalid(string message) => new(400, message);
    public static RetailException Missing(string message) => new(404, message);
    public static RetailException Conflict(string message) => new(409, message);
}
