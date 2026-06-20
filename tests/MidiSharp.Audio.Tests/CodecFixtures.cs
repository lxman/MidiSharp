using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MidiSharp.Audio.Tests;

/// <summary>
/// Shared test scaffolding: a deterministic signal, a 16-bit WAV writer, and an
/// ffmpeg transcoder. Decoder tests synthesize a signal, write it as WAV, then
/// transcode it to AIFF/FLAC/Ogg with ffmpeg and check our decoders reproduce it
/// — sample-exact for the lossless formats. ffmpeg is the trusted reference
/// encoder; tests skip if it isn't installed.
/// </summary>
internal static class CodecFixtures
{
    public const int SampleRate = 44100;

    private static readonly Lazy<bool> _ffmpeg = new(() =>
    {
        try { return Run("ffmpeg", "-version"); }
        catch { return false; }
    });

    public static bool FfmpegAvailable => _ffmpeg.Value;

    /// <summary>Deterministic stereo signal: two detuned tones with a slow fade — covers the full range without clipping.</summary>
    public static float[] MakeSignal(int frames, int channels)
    {
        var s = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)SampleRate;
            var env = 0.6 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / frames)); // hann-ish, peaks ~0.6
            var l = (float)(env * Math.Sin(2 * Math.PI * 440.0 * t));
            var r = (float)(env * Math.Sin(2 * Math.PI * 660.0 * t));
            s[i * channels] = l;
            if (channels > 1) s[i * channels + 1] = r;
        }
        return s;
    }

    public static void WriteWav16(string path, float[] interleaved, int channels)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        var frames = interleaved.Length / channels;
        var dataBytes = interleaved.Length * 2;

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(SampleRate);
        w.Write(SampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var v in interleaved)
        {
            var s = (int)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f);
            w.Write((short)Math.Clamp(s, short.MinValue, short.MaxValue));
        }
    }

    /// <summary>Transcode with ffmpeg (overwrites output). Returns false if ffmpeg fails.</summary>
    public static bool Transcode(string inputWav, string output, string? extraArgs = null)
        => Run("ffmpeg", $"-y -i \"{inputWav}\" {extraArgs} \"{output}\"");

    private static bool Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }
}
