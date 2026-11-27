using Retail.Admin;
using Retail.Admin.Components;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<EditorSessions>();
builder.Services.AddHttpClient("RetailApi", client =>
{
    var baseUrl = builder.Configuration["RetailApi:BaseUrl"] ?? "http://localhost:5100";
    client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();
app.UseAntiforgery();
EditorSessions.MapEndpoints(app);
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
