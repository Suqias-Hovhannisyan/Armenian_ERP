using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retail.Api;

internal sealed record CreateFieldRequest(string? Key, string? Label, string? Type, string? TargetModelKey);
internal sealed record PlaceFieldRequest([property: JsonRequired] int? Position);
internal sealed record ManagerEditorRequest(bool? CanEdit);
internal sealed record ManagerFieldRequest(bool? CanUse);
internal sealed record SetFieldValueRequest(JsonElement Value);

internal sealed class RequireModelEditorToken;

internal static class ModelEditorEndpoints
{
    public static void MapModelEditor(this WebApplication app)
    {
        var tokenRequirement = new RequireModelEditorToken();
        var editor = app.MapGroup("/api/model-editor/products").WithMetadata(tokenRequirement);
        var products = app.MapGroup("/api/products").WithMetadata(tokenRequirement);

        editor.MapGet("/", async (HttpContext context, ProductModelEditor editor, CancellationToken token) =>
            Results.Ok(await editor.GetEditorAsync(Role(context), token)));

        editor.MapPost("/fields", async (CreateFieldRequest request, HttpContext context, ProductModelEditor editor, CancellationToken token) =>
            Results.Json(await editor.CreateFieldAsync(Role(context), request, token), statusCode: 201));

        editor.MapPut("/fields/{key}/placement", async (string key, PlaceFieldRequest request, HttpContext context, ProductModelEditor editor, CancellationToken token) =>
        {
            await editor.PlaceFieldAsync(Role(context), key, request.Position, token);
            return Results.NoContent();
        });

        editor.MapPut("/permissions/manager", async (ManagerEditorRequest request, HttpContext context, ProductModelEditor editor, CancellationToken token) =>
        {
            await editor.SetManagerEditorAsync(Role(context), request.CanEdit, token);
            return Results.NoContent();
        });

        editor.MapPut("/fields/{key}/permissions/manager", async (string key, ManagerFieldRequest request, HttpContext context, ProductModelEditor editor, CancellationToken token) =>
        {
            await editor.SetManagerFieldAsync(Role(context), key, request.CanUse, token);
            return Results.NoContent();
        });

        products.MapGet("/form", async (HttpContext context, ProductModelEditor editor, CancellationToken token) =>
            Results.Ok(await editor.GetFormAsync(Role(context), token)));

        products.MapGet("/form/{key}/options", async (string key, HttpContext context, ProductModelEditor editor, CancellationToken token) =>
            Results.Ok(await editor.GetOptionsAsync(Role(context), key, token)));

        products.MapGet("/{id:guid}/custom-fields", async (Guid id, HttpContext context, ProductModelEditor editor, CancellationToken token) =>
            Results.Ok(await editor.GetValuesAsync(Role(context), id, token)));

        products.MapPut("/{id:guid}/custom-fields/{key}", async (Guid id, string key, SetFieldValueRequest request, HttpContext context, ProductModelEditor editor, CancellationToken token) =>
        {
            await editor.SetValueAsync(Role(context), id, key, request.Value, token);
            return Results.NoContent();
        });
    }

    public static async Task AuthorizeModelEditorRequest(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<RequireModelEditorToken>() is not null)
        {
            context.Response.Headers.CacheControl = "no-store";
            var access = context.RequestServices.GetRequiredService<ModelEditorAccess>();
            context.Items[typeof(RetailRole)] = access.RequireRole(context);
        }

        await next(context);
    }

    private static RetailRole Role(HttpContext context) => (RetailRole)context.Items[typeof(RetailRole)]!;
}
