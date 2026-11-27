using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace Retail.Admin.Components.Pages;

public partial class Pos
{
    [Inject] private IHttpClientFactory ClientFactory { get; set; } = default!;
    private Product[] products = [];
    private readonly List<CartLine> cart = [];
    private ElementReference scanInput;
    private string selectedId = "", scan = "", codes = "";
    private decimal quantity = 1;
    private Profile? profile;
    private Receipt? receipt;
    private string? error, notice;
    private Guid? pendingId;
    private bool busy;
    private bool Locked => busy || pendingId.HasValue;

    protected override Task OnInitializedAsync() => RunAsync(async client =>
        products = await client.GetFromJsonAsync<Product[]>("/api/products") ?? []);

    private string ProductName(Guid id) =>
        products.FirstOrDefault(item => item.Id == id)?.Name ?? id.ToString();

    private Task SelectAsync()
    {
        profile = null;
        codes = "";
        quantity = 1;
        if (!Guid.TryParse(selectedId, out var id)) return Task.CompletedTask;
        return RunAsync(async client => profile = await GetProfileAsync(client, id));
    }

    private static async Task<Profile> GetProfileAsync(HttpClient client, Guid id) =>
        await client.GetFromJsonAsync<Profile>($"/api/products/{id}/identifiers") ?? throw new JsonException();

    private Task ScanAsync() => RunAsync(async client =>
    {
        if (pendingId.HasValue) return;
        var raw = scan.TrimEnd('\r', '\n');
        var payload = StripScannerPrefix(raw);
        bool isMark = raw.StartsWith("]d2", StringComparison.Ordinal) ||
            (payload.Length >= 19 && payload.StartsWith("01", StringComparison.Ordinal) &&
            payload.AsSpan(16, 2).SequenceEqual("21") && payload.AsSpan(2, 14).ContainsAnyExceptInRange('0', '9') == false);
        if (isMark && payload.Length < 19)
            throw new PosError("Ստուգիր Data Matrix կոդը և կրկին սկանավորիր։");
        var lookup = isMark ? payload.Substring(2, 14) : raw.Trim();
        if (lookup.Length == 0) throw new PosError("Մուտքագրիր ապրանքի շտրիխկոդը։");
        var product = await client.GetFromJsonAsync<Product>($"/api/products/by-barcode/{Uri.EscapeDataString(lookup)}")
            ?? throw new JsonException();
        var setting = await GetProfileAsync(client, product.Id);
        if (setting.RequiresMarking && !isMark)
        {
            if (!products.Any(item => item.Id == product.Id)) products = [.. products, product];
            selectedId = product.Id.ToString();
            profile = setting;
            codes = "";
            quantity = 1;
            notice = "Սկանավորիր յուրաքանչյուր միավորի Data Matrix կոդը։";
        }
        else
        {
            Add(product, setting, 1, isMark ? [raw] : []);
        }
        scan = "";
        focusScanner = true;
    });

    private bool focusScanner;
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusScanner)
        {
            focusScanner = false;
            await scanInput.FocusAsync();
        }
    }

    private Task AddSelected() => RunAsync(_ =>
    {
        if (pendingId.HasValue) return Task.CompletedTask;
        var product = products.FirstOrDefault(item => item.Id.ToString() == selectedId);
        if (product is null || profile is null) throw new PosError("Ընտրիր ապրանք։");
        var marks = codes.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Add(product, profile, profile.RequiresMarking ? marks.Length : quantity, marks);
        codes = "";
        quantity = 1;
        return Task.CompletedTask;
    });

    private static string StripScannerPrefix(string value) =>
        value.StartsWith("]d2", StringComparison.Ordinal) ? value[3..] : value;
    private static string MarkIdentity(string value) => StripScannerPrefix(value).Split((char)29)[0];

    private void Add(Product product, Profile setting, decimal count, string[] marks)
    {
        if (count <= 0 || decimal.Round(count, 3) != count)
            throw new PosError("Մուտքագրիր դրական քանակ՝ առավելագույնը երեք տասնորդական նիշով։");
        if (setting.RequiresMarking && (marks.Length != count || marks.Any(mark =>
            !StripScannerPrefix(mark).StartsWith($"01{setting.Gtin}21", StringComparison.Ordinal))))
            throw new PosError("Յուրաքանչյուր միավորի համար սկանավորիր ընտրված ապրանքի Data Matrix կոդը։");
        if (!setting.RequiresMarking && marks.Length != 0)
            throw new PosError("Ընտրված ապրանքի համար Data Matrix գրանցված չէ։");
        var identities = cart.SelectMany(line => line.Codes).Select(MarkIdentity).ToHashSet(StringComparer.Ordinal);
        if (marks.Any(mark => !identities.Add(MarkIdentity(mark))))
            throw new PosError("Նույն միավորի Data Matrix կոդն արդեն սկանավորվել է։");
        var index = cart.FindIndex(line => line.Product.Id == product.Id);
        var existing = index < 0 ? null : cart[index];
        if (index < 0 && cart.Count >= 500)
            throw new PosError("Մեկ վաճառքում կարող է լինել առավելագույնը 500 տարբեր ապրանք։");
        var combined = (existing?.Quantity ?? 0) + count;
        if (combined > 1_000_000m)
            throw new PosError("Մեկ ապրանքի առավելագույն քանակը 1 000 000 է։");
        var line = new CartLine(product, combined, [.. existing?.Codes ?? [], .. marks]);
        if (index < 0) cart.Add(line);
        else cart[index] = line;
        notice = $"«{product.Name}» ապրանքն ավելացվել է զամբյուղում։";
    }

    private void Remove(CartLine line)
    {
        if (Locked) return;
        cart.Remove(line);
        error = null;
        notice = $"«{line.Product.Name}» ապրանքը հեռացվել է։";
    }

    private Task CheckoutAsync() => RunAsync(async client =>
    {
        if (cart.Count == 0) return;
        pendingId ??= Guid.NewGuid();
        var body = new
        {
            requestId = pendingId.Value,
            lines = cart.Select(line => new { productId = line.Product.Id, line.Quantity, markingCodes = line.Codes })
        };
        using var response = await client.PostAsJsonAsync("/api/sales/cart", body);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
                pendingId = null;
            var detail = await RetailApiErrors.DetailAsync(response);
            if (detail is not null) throw new PosError(detail);
            response.EnsureSuccessStatusCode();
        }
        var result = await response.Content.ReadFromJsonAsync<Receipt>();
        if (result is null || result.Id == Guid.Empty || result.Lines?.Length != cart.Count)
            throw new JsonException();
        receipt = result;
        cart.Clear();
        pendingId = null;
        notice = $"Վաճառքը գրանցվել է։ Ընդամենը՝ {result.TotalAmd} ֏։";
        focusScanner = true;
    });

    private async Task RunAsync(Func<HttpClient, Task> action)
    {
        if (busy) return;
        busy = true;
        error = notice = null;
        try
        {
            using var client = ClientFactory.CreateClient("RetailApi");
            await action(client);
        }
        catch (PosError ex) { error = ex.Message; }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound && !pendingId.HasValue)
        {
            error = "Ապրանքը չի գտնվել։ Ստուգիր կոդը կամ ընտրիր ցանկից։";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            error = pendingId.HasValue
                ? "Վաճառքի հաստատումը չի ստացվել։ Սեղմիր «Ստուգել վաճառքի հաստատումը»՝ նույն վաճառքը կրկին ստուգելու համար։"
                : "Տվյալները չհաջողվեց ստանալ։ Ստուգիր կապը և կրկին փորձիր։";
        }
        finally { busy = false; }
    }

    private sealed class PosError(string message) : Exception(message);
    private sealed record Product(Guid Id, string Name, string Sku, long UnitPriceAmd);
    private sealed record Profile(bool RequiresMarking, string? Gtin);
    private sealed record CartLine(Product Product, decimal Quantity, string[] Codes)
    {
        public decimal Total => decimal.Round(Quantity * Product.UnitPriceAmd, 0, MidpointRounding.AwayFromZero);
    }
    private sealed record Receipt(Guid Id, long TotalAmd, ReceiptLine[] Lines);
    private sealed record ReceiptLine(Guid Id, Guid ProductId, decimal Quantity, long TotalAmd);
}
