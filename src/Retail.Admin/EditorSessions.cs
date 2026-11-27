using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Retail.Admin;

internal sealed class EditorSessions(IMemoryCache cache, IHttpClientFactory clients)
{
    private readonly record struct SessionKey(string Id);
    private const string CookieName = "Retail.EditorSession";
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public string? Current(HttpContext context)
    {
        var id = context.Request.Cookies[CookieName];
        return id is { Length: 64 } && cache.TryGetValue(new SessionKey(id), out _) ? id : null;
    }

    public HttpClient CreateClient(string sessionId)
    {
        if (!cache.TryGetValue<string>(new SessionKey(sessionId), out var token) || token is null)
            throw new HttpRequestException("Editor session expired.", null, HttpStatusCode.Unauthorized);

        var client = clients.CreateClient("RetailApi");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    public void Revoke(string sessionId) => cache.Remove(new SessionKey(sessionId));

    public static void MapEndpoints(WebApplication app)
    {
        app.MapPost("/editor-session/login", async (HttpContext context, [FromForm] string accessKey,
            [FromForm] Guid? productId, EditorSessions sessions) =>
            await sessions.LoginAsync(context, accessKey, productId));
        app.MapPost("/editor-session/logout", async Task<IResult> (HttpContext context, EditorSessions sessions, IAntiforgery antiforgery) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
                return Results.BadRequest();
            if (sessions.Current(context) is { } id)
                sessions.Revoke(id);
            context.Response.Cookies.Delete(CookieName, CookieSettings(context));
            return Results.LocalRedirect("/model-editor");
        });
    }

    private async Task<IResult> LoginAsync(HttpContext context, string accessKey, Guid? productId)
    {
        var location = productId.HasValue ? $"/model-editor?productId={productId:D}" : "/model-editor";
        IResult Reject(string error) => Results.LocalRedirect(
            Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(location, "loginError", error));

        var token = accessKey.Trim();
        if (token.Length is < 32 or > 512 || token.Any(c => c <= ' ' || c > '~' || c == ','))
            return Reject("key");

        using var client = clients.CreateClient("RetailApi");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        try
        {
            using var response = await client.GetAsync("/api/model-editor/products", context.RequestAborted);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Reject("key");
            if (!response.IsSuccessStatusCode)
                return Reject("api");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Reject("api");
        }

        if (Current(context) is { } previousId)
            Revoke(previousId);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        cache.Set(new SessionKey(id), token, Lifetime);
        var cookie = CookieSettings(context);
        cookie.MaxAge = Lifetime;
        context.Response.Cookies.Append(CookieName, id, cookie);
        return Results.LocalRedirect(location);
    }

    private static CookieOptions CookieSettings(HttpContext context) => new()
    {
        Path = "/",
        HttpOnly = true,
        Secure = context.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        IsEssential = true
    };
}
