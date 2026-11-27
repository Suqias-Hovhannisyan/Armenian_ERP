using Retail.Api;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Retail")
    ?? throw new InvalidOperationException("Configure ConnectionStrings__Retail before starting the API.");

builder.Services.AddSingleton(new RetailStore(connectionString));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(new ModelEditorAccess(builder.Configuration));
builder.Services.AddSingleton(new ProductModelEditor(connectionString));

var app = builder.Build();
app.UseExceptionHandler();
app.UseRouting();
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (RetailException exception)
    {
        await Results.Problem(statusCode: exception.StatusCode, detail: exception.Message).ExecuteAsync(context);
    }
});

app.Use(ModelEditorEndpoints.AuthorizeModelEditorRequest);

app.MapGet("/api/products", async (RetailStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListProductsAsync(cancellationToken)));

app.MapGet("/api/products/{id:guid}/stock", async (Guid id, RetailStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetStockAsync(id, cancellationToken)));

app.MapPost("/api/products", async (CreateProductRequest request, RetailStore store, CancellationToken cancellationToken) =>
    Results.Content(await store.CreateProductAsync(request, cancellationToken), "application/json", statusCode: 201));

app.MapPost("/api/receipts", async (StockRequest request, RetailStore store, CancellationToken cancellationToken) =>
    Results.Content(await store.ReceiveAsync(request, cancellationToken), "application/json", statusCode: 201));

app.MapPost("/api/sales", async (StockRequest request, RetailStore store, CancellationToken cancellationToken) =>
    Results.Content(await store.SellAsync(request, cancellationToken), "application/json", statusCode: 201));

app.MapPost("/api/sales/cart", async (CartRequest request, RetailStore store, CancellationToken cancellationToken) =>
    Results.Content(await store.CheckoutAsync(request, cancellationToken), "application/json", statusCode: 201));

app.MapPost("/api/sales/{id:guid}/returns", async (Guid id, ReturnRequest request, RetailStore store, CancellationToken cancellationToken) =>
    Results.Content(await store.ReturnAsync(id, request, cancellationToken), "application/json", statusCode: 201));

app.MapModelEditor();
CatalogModels.MapEndpoints(app, connectionString);
ProductIdentifiers.MapEndpoints(app, connectionString);

app.Run();
