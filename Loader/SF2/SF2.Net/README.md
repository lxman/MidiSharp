# SFNet

A pure-managed .NET library for reading, querying, and repackaging SoundFont 2 (`.sf2`) files. Targets `netstandard2.1`.

This is a .NET port of the Qt-based [sflib](https://github.com/) library, exposing an idiomatic C# API.

## Quick start

```csharp
using SFNet;

var sf = SoundFont.Load("MySoundFont.sf2");

foreach (var preset in sf.Presets)
    Console.WriteLine($"Bank {preset.Bank} Preset {preset.Number}: {preset.Name}");

// Extract a single preset into a fresh, self-contained SF2 byte array
byte[] extracted = sf.ExtractPreset(bank: 0, preset: 0);
File.WriteAllBytes("piano.sf2", extracted);
```

## Features

- Parse the RIFF/sfbk container (`INFO`, `sdta`, `pdta`).
- Enumerate presets, instruments, zones, generators, modulators, and samples.
- Read 16-bit and 24-bit sample data.
- Extract a single preset to a standalone `.sf2` byte array.
- Strict validation against the SoundFont 2.1 specification.

## License

MIT.
