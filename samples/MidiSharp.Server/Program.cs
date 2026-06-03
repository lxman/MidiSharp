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
// Lazy filesystem browse for the file picker: one directory level at a time, filtered by kind.
// Starts at the configured root when no path is given, but the user may navigate anywhere.
app.MapGet("/api/browse", (string? kind, string? path) =>
{
    var (exts, start) = kind == "midi"
        ? (new[] { ".mid", ".midi" }, midiRoot)
        : (new[] { ".sf2", ".sf3", ".sfz", ".dls" }, sfRoot);
    return Results.Json(Browse(path, start, exts));
});
// Patches the chosen song uses, named against the chosen base font ("what it normally plays").
app.MapGet("/api/patches", (string midiPath, string soundfontPath) =>
{
    try { return Results.Json(player.GetSongPatches(midiPath, soundfontPath)); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }); }
});
// A source font's instrument catalog, for the per-patch override picker.
app.MapGet("/api/soundfont-patches", (string path) =>
{
    try { return Results.Json(player.GetSoundfontPatches(path)); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }); }
});
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

// List a single directory level for the file browser: visible sub-folders and files matching
// the kind's extensions, plus the parent for "up" navigation. Falls back to the configured
// root (then home, then "/") when the requested path is missing or gone.
static object Browse(string? path, string startDefault, string[] exts)
{
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string? FirstExisting(params string?[] cands)
    {
        foreach (var c in cands)
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;
        return null;
    }

    var dir = FirstExisting(path, startDefault, home, "/");
    if (dir == null) return new { error = "No accessible directory found." };
    var full = Path.GetFullPath(dir);

    static bool Visible(string p) { var n = Path.GetFileName(p); return n.Length > 0 && n[0] != '.'; }
    try
    {
        var dirs = Directory.GetDirectories(full).Where(Visible)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(p => new { name = Path.GetFileName(p), path = p }).ToArray();
        var files = Directory.GetFiles(full)
            .Where(p => Visible(p) && exts.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(p => new { name = Path.GetFileName(p), path = p }).ToArray();
        return new { path = full, parent = Directory.GetParent(full)?.FullName, dirs, files };
    }
    catch (Exception ex)
    {
        return new { error = ex.Message, path = full };
    }
}
