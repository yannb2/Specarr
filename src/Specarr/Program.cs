using System.Globalization;
using Arrkit.Hosting;
using Arrkit.Logging;
using Specarr.Components;
using Specarr.Library;
using Specarr.Media;
using Specarr.Options;
using Specarr.Tools;

// Numbers and dates the same way everywhere (23.976 fps, not 23,976).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// Tools get UTF-8 file names ("Amélie", "Me²"), whatever the container's locale.
Arrkit.Processes.Utf8Locale.Ensure();

var builder = WebApplication.CreateBuilder(args);

// ASP.NET's data-protection keys (antiforgery tokens) in /config/keys, so they survive an update.
var configFolder = Path.GetDirectoryName(builder.Configuration["Specarr:DatabasePath"] ?? "/config/specarr.db") ?? "/config";
DataProtectionKeys.PersistKeysIn(builder, configFolder, "Specarr");

// The Logs page: the app's recent log lines, in memory.
var memoryLog = new MemoryLog();
// Also in /config/logs, so the Logs page's second tab has the last lines from before a restart
// (how many: Logs__KeepLines, default 2000).
memoryLog.KeepFile(Path.Combine(configFolder, "logs", "specarr.jsonl"),
    builder.Configuration.GetValue(MemoryLog.KeepLinesKey, MemoryLog.Capacity));
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
