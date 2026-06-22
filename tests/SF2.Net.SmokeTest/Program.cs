using System.Diagnostics;
using MidiSharp.Loader.Sf2;
using MidiSharp.Loader.Sf2.Model;
using SF2.Net.SmokeTest;

if (args.Length >= 2 && args[0] == "inspect")
{
    Inspect.DumpChunks(args[1]);
    return;
}
if (args.Length >= 2 && args[0] == "inst")
{
    InspectInst.Dump(args[1]);
    return;
}
if (args.Length >= 3 && args[0] == "bag")
{
    InspectBag.Dump(args[2], args[1]);
    return;
}
if (args.Length >= 2 && args[0] == "phdr")
{
    InspectPhdr.Dump(args[1]);
    return;
}

if (args.Length >= 1 && args[0] == "extract")
{
    string extractRoot = args.Length > 1 ? args[1] : Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "soundfonts");
    int? extractLimit = args.Length > 2 && int.TryParse(args[2], out int en) ? en : null;
    RunExtract(extractRoot, extractLimit);
    return;
}

if (args.Length >= 1 && args[0] == "validate")
{
    string validateRoot = args.Length > 1 ? args[1] : Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "soundfonts");
    int? validateLimit = args.Length > 2 && int.TryParse(args[2], out int vn) ? vn : null;
    RunSampleValidation(validateRoot, validateLimit);
    return;
}

if (args.Length >= 2 && args[0] == "list-broken")
{
    string listRoot = args[1];
    string outFile = args.Length > 2 ? args[2] : "/tmp/playback_broken.txt";
    ListPlaybackBroken(listRoot, outFile);
    return;
}

if (args.Length >= 2 && args[0] == "find-pitch")
{
    foreach (string p in Directory.EnumerateFiles(args[1], "*.sf2", SearchOption.AllDirectories))
    {
        SoundFont sf; try { sf = SoundFont.Load(p); } catch { continue; }
        List<(SampleHeader s, int i)> bad = sf.SampleHeaders.Select((s, i) => (s, i)).Where(t => t.s.OriginalPitch > 127 && t.s.OriginalPitch != 255).Take(5).ToList();
        if (bad.Count == 0) continue;
        Console.WriteLine($"{p}");
        foreach ((SampleHeader s, int i) in bad)
            Console.WriteLine($"  [{i}] '{s.Name}'  OriginalPitch={s.OriginalPitch}  Type={s.SampleType}  Rate={s.SampleRate}");
    }
    return;
}

string root = args.Length > 0 ? args[0] : Path.Combine(Environment.GetEnvironmentVariable("HOME")!, "soundfonts");
int? limit = args.Length > 1 && int.TryParse(args[1], out int n) ? n : null;

string[] files = Directory.EnumerateFiles(root, "*.sf2", SearchOption.AllDirectories).ToArray();
if (limit is { } lim) files = files.Take(lim).ToArray();

Console.WriteLine($"Scanning {files.Length} files under {root}");

int ok = 0, failed = 0, crashed = 0;
var byCode = new Dictionary<string, int>();
var sample = new Dictionary<string, string>();
var sw = Stopwatch.StartNew();

foreach (string path in files)
{
    try
    {
        SoundFont sf = SoundFont.Load(path);
        _ = sf.Presets.Count;
        _ = sf.Instruments.Count;
        _ = sf.Banks;
        ok++;
    }
    catch (SoundFontException ex)
    {
        failed++;
        string code = string.Join(",", ex.Codes);
        byCode[code] = byCode.GetValueOrDefault(code) + 1;
        sample.TryAdd(code, path);
        if (Environment.GetEnvironmentVariable("LIST_FAILURES") == "1" && code != "FileBroken")
            Console.WriteLine($"  [{code}] {path}");
        if (Environment.GetEnvironmentVariable("LIST_BROKEN") == "1" && code == "FileBroken")
            Console.WriteLine($"  [{code}] {path}");
    }
    catch (Exception ex)
    {
        crashed++;
        string code = ex.GetType().Name;
        byCode[code] = byCode.GetValueOrDefault(code) + 1;
        if (!sample.ContainsKey(code))
            sample[code] = $"{path}  ::  {ex.Message}";
    }
}

sw.Stop();

Console.WriteLine();
Console.WriteLine($"== Summary ({sw.Elapsed.TotalSeconds:0.0}s) ==");
Console.WriteLine($"  OK:      {ok}");
Console.WriteLine($"  Failed:  {failed}  (clean SoundFontException)");
Console.WriteLine($"  Crashed: {crashed}  (unhandled exception)");
Console.WriteLine();
Console.WriteLine("Failure breakdown:");
foreach (KeyValuePair<string, int> kv in byCode.OrderByDescending(k => k.Value))
{
    Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
    Console.WriteLine($"          first: {sample[kv.Key]}");
}

static void ListPlaybackBroken(string root, string outFile)
{
    // Categories that genuinely break audio playback. Excludes:
    //   - SampleRateOutOfRange  (high-rate samples like 96/192 kHz are usable by any modern player)
    //   - OriginalPitchInvalid  (players degrade to a default pitch)
    //   - All the cosmetic loop-margin / sample-length / "no loop" sentinel issues
    var playbackBreaking = new HashSet<SampleValidationCode>
    {
        SampleValidationCode.StartNotBeforeEnd,
        SampleValidationCode.SampleExceedsSmpl,
        SampleValidationCode.LoopStartBeforeSampleStart,
        SampleValidationCode.LoopEndAfterSampleEnd,
        SampleValidationCode.StereoLinkInvalid,
        SampleValidationCode.StereoLinkTypeMismatch,
        SampleValidationCode.StereoLinkLengthMismatch,
    };

    string[] files = Directory.EnumerateFiles(root, "*.sf2", SearchOption.AllDirectories).ToArray();
    Console.WriteLine($"Scanning {files.Length} files for playback-breaking sample defects");

    var broken = new List<(string path, int issueCount, HashSet<SampleValidationCode> categories)>();
    int filesScanned = 0, filesSkipped = 0;
    var sw = Stopwatch.StartNew();

    foreach (string path in files)
    {
        SoundFont sf;
        try { sf = SoundFont.Load(path); }
        catch { filesSkipped++; continue; }

        filesScanned++;
        List<SampleValidationIssue> realIssues = sf.ValidateSamples()
            .Where(i => playbackBreaking.Contains(i.Code))
            .ToList();
        if (realIssues.Count == 0) continue;

        var cats = new HashSet<SampleValidationCode>(realIssues.Select(i => i.Code));
        broken.Add((path, realIssues.Count, cats));
    }

    sw.Stop();
    File.WriteAllLines(outFile, broken.Select(b => b.path));

    Console.WriteLine();
    Console.WriteLine($"== Result ({sw.Elapsed.TotalSeconds:0.0}s) ==");
    Console.WriteLine($"  Files scanned:               {filesScanned}");
    Console.WriteLine($"  Files skipped (load failed): {filesSkipped}");
    Console.WriteLine($"  Files with playback defects: {broken.Count}");
    Console.WriteLine($"  Path list written to:        {outFile}");
    Console.WriteLine();
    Console.WriteLine("Distribution by defect-category combination:");
    foreach (IGrouping<string, (string path, int issueCount, HashSet<SampleValidationCode> categories)> g in broken
        .GroupBy(b => string.Join('+', b.categories.OrderBy(c => c.ToString()).Select(c => c.ToString())))
        .OrderByDescending(g => g.Count())
        .Take(15))
        Console.WriteLine($"  {g.Count(),5}  {g.Key}");
}

static void RunSampleValidation(string root, int? limit)
{
    // SF2-spec violations that are pervasive in real-world fonts and that the original sflib
    // silently auto-corrects on extract — treating them as "real defects" would label almost
    // every soundfont in existence as broken.
    var cosmeticCodes = new HashSet<SampleValidationCode>
    {
        SampleValidationCode.LoopEndMarginTooSmall,
        SampleValidationCode.LoopStartMarginTooSmall,
        SampleValidationCode.LoopStartNotBeforeLoopEnd, // common "no loop" sentinel
        SampleValidationCode.LoopTooShort,
        SampleValidationCode.SampleRateOutOfRange,
        SampleValidationCode.SampleTooShort,
    };

    string[] files = Directory.EnumerateFiles(root, "*.sf2", SearchOption.AllDirectories).ToArray();
    if (limit is { } lim) files = files.Take(lim).ToArray();

    Console.WriteLine($"Sample-validation pass over {files.Length} files");

    int filesScanned = 0, filesSkipped = 0, filesClean = 0, filesWithIssues = 0;
    int filesCosmeticOnly = 0, filesWithRealDefect = 0;
    int totalSamples = 0, totalIssues = 0;
    var byCode = new Dictionary<SampleValidationCode, int>();
    var firstExample = new Dictionary<SampleValidationCode, string>();
    var worstOffenders = new List<(string path, int issueCount, int sampleCount)>();
    var realDefectFiles = new List<(string path, int realIssueCount, HashSet<SampleValidationCode> realCategories)>();
    var sw = Stopwatch.StartNew();

    foreach (string path in files)
    {
        SoundFont sf;
        try { sf = SoundFont.Load(path); }
        catch { filesSkipped++; continue; }

        filesScanned++;
        totalSamples += sf.SampleHeaders.Count;
        IReadOnlyList<SampleValidationIssue> issues = sf.ValidateSamples();
        if (issues.Count == 0) { filesClean++; continue; }

        filesWithIssues++;
        totalIssues += issues.Count;
        worstOffenders.Add((path, issues.Count, sf.SampleHeaders.Count));
        var realCount = 0;
        var realCats = new HashSet<SampleValidationCode>();
        foreach (SampleValidationIssue iss in issues)
        {
            byCode[iss.Code] = byCode.GetValueOrDefault(iss.Code) + 1;
            if (!firstExample.ContainsKey(iss.Code))
                firstExample[iss.Code] = $"{path}  {iss}";
            if (!cosmeticCodes.Contains(iss.Code))
            {
                realCount++;
                realCats.Add(iss.Code);
            }
        }
        if (realCount > 0)
        {
            filesWithRealDefect++;
            realDefectFiles.Add((path, realCount, realCats));
        }
        else filesCosmeticOnly++;
    }

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine($"== Sample validation summary ({sw.Elapsed.TotalSeconds:0.0}s) ==");
    Console.WriteLine($"  Files scanned:        {filesScanned}");
    Console.WriteLine($"  Files skipped:        {filesSkipped}");
    Console.WriteLine($"  Files clean (no issues at all):     {filesClean}");
    Console.WriteLine($"  Files with cosmetic issues only:    {filesCosmeticOnly}");
    Console.WriteLine($"  Files with real defects:            {filesWithRealDefect}");
    Console.WriteLine($"  Files with issues (any):            {filesWithIssues}");
    Console.WriteLine($"  Total samples:        {totalSamples:N0}");
    Console.WriteLine($"  Total issues:         {totalIssues:N0}");
    Console.WriteLine();
    Console.WriteLine("Issues by category:");
    foreach (KeyValuePair<SampleValidationCode, int> kv in byCode.OrderByDescending(k => k.Value))
    {
        Console.WriteLine($"  {kv.Value,8:N0}  {kv.Key}");
        Console.WriteLine($"             first: {firstExample[kv.Key]}");
    }
    Console.WriteLine();
    Console.WriteLine("Top 10 worst offenders (most issues, all categories):");
    foreach ((string path, int ic, int sc) in worstOffenders.OrderByDescending(t => t.issueCount).Take(10))
        Console.WriteLine($"  {ic,6} issues across {sc,5} samples  {path}");

    Console.WriteLine();
    Console.WriteLine("Distribution of real-defect categories per file:");
    IOrderedEnumerable<IGrouping<string, (string path, int realIssueCount, HashSet<SampleValidationCode> realCategories)>> distinctRealCats = realDefectFiles
        .GroupBy(f => string.Join('+', f.realCategories.Select(c => c.ToString()).OrderBy(s => s)))
        .OrderByDescending(g => g.Count());
    foreach (IGrouping<string, (string path, int realIssueCount, HashSet<SampleValidationCode> realCategories)> g in distinctRealCats.Take(15))
        Console.WriteLine($"  {g.Count(),5}  {g.Key}");
}

static void RunExtract(string root, int? limit)
{
    string[] files = Directory.EnumerateFiles(root, "*.sf2", SearchOption.AllDirectories).ToArray();
    if (limit is { } lim) files = files.Take(lim).ToArray();

    Console.WriteLine($"Extract test over {files.Length} files");
    int filesOk = 0, filesSkipped = 0, filesCrashed = 0;
    int totalPresets = 0, extractedOk = 0, extractedEmpty = 0, extractParseOk = 0, extractParseFail = 0;
    var firstError = new Dictionary<string, string>();
    var sw = Stopwatch.StartNew();

    foreach (string path in files)
    {
        SoundFont sf;
        try { sf = SoundFont.Load(path); }
        catch { filesSkipped++; continue; }

        filesOk++;
        foreach (Preset preset in sf.Presets)
        {
            totalPresets++;
            try
            {
                byte[] extracted = sf.ExtractPreset(preset.Bank, preset.Number);
                if (extracted.Length == 0) { extractedEmpty++; continue; }
                extractedOk++;
                try
                {
                    SoundFont reloaded = SoundFont.Load(extracted);
                    if (reloaded.Presets.Count >= 1) extractParseOk++;
                    else extractParseFail++;
                }
                catch (Exception ex)
                {
                    extractParseFail++;
                    var key = $"reparse:{ex.GetType().Name}:{(ex is SoundFontException sfe ? string.Join(',', sfe.Codes) : ex.Message[..Math.Min(60, ex.Message.Length)])}";
                    firstError.TryAdd(key, $"{path}  preset={preset.Bank}/{preset.Number}");
                }
            }
            catch (Exception ex)
            {
                filesCrashed++;
                var key = $"extract:{ex.GetType().Name}";
                firstError.TryAdd(key, $"{path}  preset={preset.Bank}/{preset.Number}  ::  {ex.Message[..Math.Min(80, ex.Message.Length)]}");
            }
        }
    }

    sw.Stop();
    Console.WriteLine();
    Console.WriteLine($"== Extract summary ({sw.Elapsed.TotalSeconds:0.0}s) ==");
    Console.WriteLine($"  Files loaded:           {filesOk}");
    Console.WriteLine($"  Files skipped (broken): {filesSkipped}");
    Console.WriteLine($"  Total presets attempted: {totalPresets}");
    Console.WriteLine($"  Extracted (non-empty):   {extractedOk}");
    Console.WriteLine($"  Extracted (empty bytes): {extractedEmpty}");
    Console.WriteLine($"  Re-parsed OK:            {extractParseOk}");
    Console.WriteLine($"  Re-parsed FAILED:        {extractParseFail}");
    Console.WriteLine($"  Extract crashes:         {filesCrashed}");
    if (firstError.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("First example per error kind:");
        foreach (KeyValuePair<string, string> kv in firstError.OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key}\n      {kv.Value}");
    }
}
