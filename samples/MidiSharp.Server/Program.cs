using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MidiSharp.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<PlayerService>();
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5005");

var app = builder.Build();

// File-browse roots (override with --midi-root / --sf-root); default to ~/soundfonts.
var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string Expand(string p) => p.StartsWith('~') ? home + p[1..] : p;
var midiRoot = Expand(builder.Configuration["midi-root"] ?? Path.Combine(home, "soundfonts"));
var sfRoot = Expand(builder.Configuration["sf-root"] ?? Path.Combine(home, "soundfonts"));

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

var player = app.Services.GetRequiredService<PlayerService>();

app.MapGet("/api/devices", () => Results.Json(player.GetDevices()));
app.MapGet("/api/midi", () => Results.Json(ListFiles(midiRoot, [".mid", ".midi"])));
app.MapGet("/api/soundfonts", () => Results.Json(ListFiles(sfRoot, [".sf2", ".sf3", ".sfz", ".dls"])));
app.MapPost("/api/play", (PlayRequest req) => Results.Json(player.Play(req)));
app.MapPost("/api/stop", () => { player.Stop(); return Results.Ok(); });
app.MapGet("/api/status", () => Results.Json(player.Status()));
app.MapPost("/api/exit", () => { player.RequestExit(); return Results.Ok(); });

// One-directional status push so the UI sees the playhead and the completion event live.
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    while (ws.State == WebSocketState.Open)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(player.Status()));
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, context.RequestAborted); }
        catch { break; }
        try { await Task.Delay(250, context.RequestAborted); } catch { break; }
    }
});

Console.WriteLine($"MidiSharp web player → {string.Join(", ", app.Urls.DefaultIfEmpty("http://localhost:5005"))}");
Console.WriteLine($"  MIDI root:      {midiRoot}");
Console.WriteLine($"  SoundFont root: {sfRoot}");
app.Run();

static object[] ListFiles(string root, string[] exts)
{
    if (!Directory.Exists(root)) return [];
    // No cap: silently dropping files would hide a user's collection. The browser's
    // native <select> typeahead copes with a few thousand entries; narrow with --sf-root.
    return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .Select(object (f) => new { path = f, name = Path.GetRelativePath(root, f) })
        .ToArray();
}
