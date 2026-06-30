using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MidiSharp.Server;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5005");

// File-browse roots (override with --midi-root / --sf-root). MIDI → ~/soundfonts;
// SoundFont → ~/soundfonts/deduped/sf2 (the deduplicated library). Navigable anywhere.
// Saved setups live in a hidden config dir (override with --setups-root).
string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string Expand(string p) => p.StartsWith('~') ? home + p[1..] : p;
string midiRoot = Expand(builder.Configuration["midi-root"] ?? Path.Combine(home, "soundfonts"));
string sfRoot = Expand(builder.Configuration["sf-root"] ?? Path.Combine(home, "soundfonts", "deduped", "sf2"));
string setupsRoot = Expand(builder.Configuration["setups-root"] ?? Path.Combine(home, ".config", "midisharp", "setups"));

builder.Services.AddSingleton<PlayerService>();
builder.Services.AddSingleton(new SetupStore(setupsRoot));

WebApplication app = builder.Build();
var setups = app.Services.GetRequiredService<SetupStore>();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

var player = app.Services.GetRequiredService<PlayerService>();

app.MapGet("/api/devices", () => Results.Json(player.GetDevices()));
// Sentinel "path" for the synthetic drives root (lists every logical drive, including mapped
// network drives). Reached via "up" from a drive root on Windows; navigable back into any drive.
const string DrivesRoot = "::drives";
// Lazy filesystem browse for the file picker: one directory level at a time, filtered by kind.
// Starts at the configured root when no path is given, but the user may navigate anywhere.
app.MapGet("/api/browse", (string? kind, string? path) =>
{
    (string[] exts, string start) = kind == "midi"
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
// The song's tracks with the instrument each currently sounds, for the per-instrument override picker.
app.MapGet("/api/tracks", (string midiPath, string soundfontPath) =>
{
    try { return Results.Json(player.GetSongTracks(midiPath, soundfontPath)); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }); }
});
// The song's mixer parts — one per (track, channel), labeled intelligently. The mixer's data source.
app.MapGet("/api/parts", (string midiPath, string soundfontPath) =>
{
    try { return Results.Json(player.GetSongParts(midiPath, soundfontPath)); }
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
// Live mixer + master control — applied to the running engine without a restart.
app.MapPost("/api/mix", (InstrumentMixDto m) => { player.SetInstrumentMix(m); return Results.Ok(); });
app.MapPost("/api/master", (MasterDto m) => { player.SetMaster(m); return Results.Ok(); });
app.MapPost("/api/insert", (InstrumentInsertDto m) => { player.SetInstrumentInsert(m); return Results.Ok(); });
// Hosted plugins (CLAP / LADSPA) discovered on the system, for the effect-rack picker. /params loads
// the chosen plugin transiently to read its parameter list so the UI can render generic knobs.
app.MapGet("/api/plugins", () => Results.Json(player.GetPlugins()));
app.MapGet("/api/plugin-info", (string format, string id) =>
{
    try { PluginInfoDto? info = player.GetPluginInfo(format, id); return info is null ? Results.NotFound() : Results.Json(info); }
    catch (Exception ex) { return Results.Json(new { error = ex.Message }); }
});
app.MapPost("/api/plugins/rescan", () => { player.RescanPlugins(); return Results.Json(player.GetPlugins()); });
// Open / close a loaded plugin's native editor window (by the insert's InstanceId). The window opens on
// the machine running the server (in the sandbox worker that holds the plugin) — for local use.
app.MapPost("/api/plugins/editor/open", (EditorRequest r) =>
    Results.Json(new { ok = player.OpenPluginEditor(r.InstanceId, r.Title ?? "Plugin editor") }));
app.MapPost("/api/plugins/editor/close", (EditorRequest r) => { player.ClosePluginEditor(r.InstanceId); return Results.Ok(); });
app.MapGet("/api/status", () => Results.Json(player.Status()));
app.MapPost("/api/exit", () => { player.RequestExit(); return Results.Ok(); });

// Saved setups (per-MIDI instrument-substitution configurations). The browser holds the working
// copy; these persist it. Save with an existing name for the same song overwrites it.
app.MapGet("/api/setups", (string midiPath) => Results.Json(setups.ListForMidi(midiPath)));
// Capture each loaded plugin's live state into the setup before persisting, so stateful plugins round-trip.
app.MapPost("/api/setups", (SetupDto setup) => Results.Json(new { id = setups.Save(player.CaptureStates(setup)) }));
app.MapGet("/api/setups/{id}", (string id) =>
{
    SetupDto? s = setups.Load(id);
    return s is null ? Results.NotFound() : Results.Json(s);
});
app.MapDelete("/api/setups/{id}", (string id) => setups.Delete(id) ? Results.Ok() : Results.NotFound());

// One-directional status push so the UI sees the playhead and the completion event live.
// Serialize with Web defaults (camelCase) to match the HTTP /api/* responses (Results.Json) and the
// client's field names — a bare JsonSerializer.Serialize would emit the DTO's PascalCase names and the
// browser would read every field as undefined (state never "playing", playhead frozen).
var wsJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using WebSocket ws = await context.WebSockets.AcceptWebSocketAsync();
    while (ws.State == WebSocketState.Open)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(player.Status(), wsJson));
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, context.RequestAborted); }
        catch { break; }
        // 20 Hz: fast enough to drive a smooth master-output meter (the client adds falloff ballistics).
        // The status payload is tiny and this is localhost, so the extra frames are negligible.
        try { await Task.Delay(50, context.RequestAborted); } catch { break; }
    }
});

Console.WriteLine($"MidiSharp web player → {string.Join(", ", app.Urls.DefaultIfEmpty("http://localhost:5005"))}");
Console.WriteLine($"  MIDI root:      {midiRoot}");
Console.WriteLine($"  SoundFont root: {sfRoot}");
Console.WriteLine($"  Setups root:    {setupsRoot}");
app.Run();

// List a single directory level for the file browser: visible sub-folders and files matching
// the kind's extensions, plus the parent for "up" navigation. Falls back to the configured
// root (then home, then "/") when the requested path is missing or gone.
static object Browse(string? path, string startDefault, string[] exts)
{
    if (path == DrivesRoot) return Drives();

    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    string? FirstExisting(params string?[] cands)
    {
        foreach (string? c in cands)
            if (!string.IsNullOrEmpty(c) && Directory.Exists(c)) return c;
        return null;
    }

    string? dir = FirstExisting(path, startDefault, home, "/");
    if (dir == null) return new { error = "No accessible directory found." };
    string full = Path.GetFullPath(dir);

    static bool Visible(string p) { string n = Path.GetFileName(p); return n.Length > 0 && n[0] != '.'; }
    try
    {
        var dirs = Directory.GetDirectories(full).Where(Visible)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(p => new { name = Path.GetFileName(p), path = p }).ToArray();
        var files = Directory.GetFiles(full)
            .Where(p => Visible(p) && exts.Contains(Path.GetExtension(p).ToLowerInvariant()))
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(p => new { name = Path.GetFileName(p), path = p }).ToArray();
        // At a drive root GetParent is null; on Windows fall back to the drives root so the user
        // can step "up" to every drive (local and mapped network) instead of hitting a dead end.
        string? parent = Directory.GetParent(full)?.FullName
                         ?? (OperatingSystem.IsWindows() ? DrivesRoot : null);
        return new { path = full, parent, dirs, files };
    }
    catch (Exception ex)
    {
        return new { error = ex.Message, path = full };
    }
}

// The synthetic drives root: every ready logical drive, surfaced as folders. DriveInfo.GetDrives
// enumerates mapped network drives alongside local volumes, so mapped drives become reachable.
static object Drives()
{
    var dirs = DriveInfo.GetDrives()
        .Where(d => { try { return d.IsReady; } catch { return false; } })
        .Select(d =>
        {
            string label;
            try { label = d.VolumeLabel; } catch { label = string.Empty; }
            return new
            {
                name = string.IsNullOrWhiteSpace(label) ? d.Name : $"{d.Name} ({label})",
                path = d.RootDirectory.FullName,
            };
        })
        .OrderBy(x => x.path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return new { path = DrivesRoot, parent = (string?)null, dirs, files = Array.Empty<object>() };
}
