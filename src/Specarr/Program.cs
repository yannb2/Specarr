using System.Globalization;
using Specarr.Components;
using Specarr.Library;
using Specarr.Logging;
using Specarr.Media;
using Specarr.Options;
using Specarr.Tools;

// Numbers and dates the same way everywhere (23.976 fps, not 23,976).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// The Logs page: the app's recent log lines, in memory.
var memoryLog = new MemoryLog();
builder.Logging.AddProvider(new MemoryLoggerProvider(memoryLog));
builder.Services.AddSingleton(memoryLog);

builder.Services.AddOptions<SpecarrOptions>()
    .Bind(builder.Configuration.GetSection(SpecarrOptions.Section))
    .PostConfigure(o => o.WithDefaults());

builder.Services.AddSingleton<IndexStore>();
builder.Services.AddSingleton<MediaProbe>();
builder.Services.AddSingleton<LibraryIndex>();
builder.Services.AddHostedService(services => services.GetRequiredService<LibraryIndex>());

builder.Services.AddSingleton<ToolStatusService>();
builder.Services.AddHostedService(services => services.GetRequiredService<ToolStatusService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/health", () => Results.Ok("ok"));

// Counts for a dashboard (Homepage's custom API widget). Read-only, no key.
app.MapGet("/api/stats", (LibraryIndex index, ToolStatusService tools) =>
    Results.Ok(LibraryStats.From(index) with { ToolUpdates = tools.UpdateCount }));
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
