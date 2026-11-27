using System.Text.Json;

namespace Retail.Admin.Components.Pages;

internal static class RetailApiErrors
{
    public static async Task<string?> DetailAsync(HttpResponseMessage response)
    {
        if ((int)response.StatusCode is < 400 or >= 500 ||
            response.Content.Headers.ContentType?.MediaType != "application/problem+json")
            return null;

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            return problem.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String
                ? detail.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
