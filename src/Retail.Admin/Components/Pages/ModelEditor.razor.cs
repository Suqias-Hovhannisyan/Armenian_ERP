using Microsoft.AspNetCore.Components;

namespace Retail.Admin.Components.Pages;

public partial class ModelEditor
{
    private const string CatalogPath = "/api/model-editor/models";
    private CatalogModel[] catalogModels = [];
    private CatalogRecord[] catalogRecords = [];
    private readonly Dictionary<string, CatalogRecord[]> referenceOptions = new();
    private string catalogModelKey = "";
    private string targetModelKey = "";
    private string modelKey = "";
    private string modelLabel = "";
    private string recordName = "";

    private Task CreateModelAsync() => RunAsync(async () =>
    {
        using var client = Sessions.CreateClient(SessionId);
        var key = modelKey.Trim();
        using var response = await client.PostAsJsonAsync(CatalogPath, new { key, label = modelLabel.Trim() });
        response.EnsureSuccessStatusCode();
        catalogModelKey = targetModelKey = key;
        modelKey = modelLabel = recordName = "";
        await ReloadCatalogAsync(client);
        message = "Մոդելը ստեղծվել է։";
    });

    private async Task ReloadCatalogAsync(HttpClient client)
    {
        catalogModels = await client.GetFromJsonAsync<CatalogModel[]>(CatalogPath) ?? [];
        await LoadRecordsAsync(client);
    }

    private Task SelectCatalogAsync(ChangeEventArgs args) => RunAsync(async () =>
    {
        catalogModelKey = args.Value?.ToString() ?? "";
        recordName = "";
        using var client = Sessions.CreateClient(SessionId);
        await LoadRecordsAsync(client);
    });

    private async Task LoadRecordsAsync(HttpClient client)
    {
        catalogRecords = [];
        if (catalogModelKey.Length > 0)
        {
            catalogRecords = await client.GetFromJsonAsync<CatalogRecord[]>($"{CatalogPath}/{catalogModelKey}/records") ?? [];
        }
    }

    private Task CreateRecordAsync() => RunAsync(async () =>
    {
        using var client = Sessions.CreateClient(SessionId);
        using var response = await client.PostAsJsonAsync($"{CatalogPath}/{catalogModelKey}/records",
            new { name = recordName.Trim() });
        response.EnsureSuccessStatusCode();
        recordName = "";
        await LoadRecordsAsync(client);
        if (loadedProduct is { } product)
        {
            await LoadProductAsync(product.Id);
        }
        message = "Գրառումն ավելացվել է։";
    });

    private sealed record CatalogModel(string Key, string Label);
    private sealed record CatalogRecord(Guid Id, string Name);
}
