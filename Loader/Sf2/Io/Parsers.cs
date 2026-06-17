using System;
using Loader.Sf2.Enums;
using Loader.Sf2.Model;

namespace Loader.Sf2.Io;

internal static class Parsers
{
    public static PresetHeaderRecord[] ParsePhdr(ReadOnlyMemory<byte> chunk)
    {
        var span = chunk.Span;
        var count = span.Length / 38;
        var result = new PresetHeaderRecord[count];
        for (var i = 0; i < count; i++)
        {
            var o = i * 38;
            result[i] = new PresetHeaderRecord
            {
                Name = BinaryHelpers.ReadFixedAscii(span, o, 20),
                Preset = BinaryHelpers.ReadUInt16LE(span, o + 20),
                Bank = BinaryHelpers.ReadUInt16LE(span, o + 22),
                BagIndex = BinaryHelpers.ReadUInt16LE(span, o + 24),
                Library = BinaryHelpers.ReadUInt32LE(span, o + 26),
                Genre = BinaryHelpers.ReadUInt32LE(span, o + 30),
                Morphology = BinaryHelpers.ReadUInt32LE(span, o + 34),
            };
        }
        return result;
    }

    public static BagRecord[] ParseBag(ReadOnlyMemory<byte> chunk)
    {
        var span = chunk.Span;
        var count = span.Length / 4;
        var result = new BagRecord[count];
        for (var i = 0; i < count; i++)
        {
            var o = i * 4;
            result[i] = new BagRecord
            {
                GenIndex = BinaryHelpers.ReadUInt16LE(span, o),
                ModIndex = BinaryHelpers.ReadUInt16LE(span, o + 2),
            };
        }
        return result;
    }

    public static Modulator[] ParseMod(ReadOnlyMemory<byte> chunk)
    {
        var span = chunk.Span;
        var count = span.Length / 10;
        var result = new Modulator[count];
        for (var i = 0; i < count; i++)
        {
            var o = i * 10;
            result[i] = new Modulator
            {
                SourceOperator = BinaryHelpers.ReadUInt16LE(span, o),
                DestinationOperator = (SFGenerator)BinaryHelpers.ReadUInt16LE(span, o + 2),
                Amount = BinaryHelpers.ReadInt16LE(span, o + 4),
                AmountSourceOperator = BinaryHelpers.ReadUInt16LE(span, o + 6),
                TransformOperator = BinaryHelpers.ReadUInt16LE(span, o + 8),
            };
        }
        return result;
    }

    public static Generator[] ParseGen(ReadOnlyMemory<byte> chunk)
    {
        var span = chunk.Span;
        var count = span.Length / 4;
        var result = new Generator[count];
        for (var i = 0; i < count; i++)
        {
            var o = i * 4;
            result[i] = new Generator(
                (SFGenerator)BinaryHelpers.ReadUInt16LE(span, o),
                new GeneratorAmount(BinaryHelpers.ReadUInt16LE(span, o + 2)));
        }
        return result;
    }

    public static InstrumentRecord[] ParseInst(ReadOnlyMemory<byte> chunk)
    {
        var span = chunk.Span;
        var count = span.Length / 22;
        var result = new InstrumentRecord[count];
        for (var i = 0; i < count; i++)
        {
            var o = i * 22;
            result[i] = new InstrumentRecord
            {
                Name = BinaryHelpers.ReadFixedAscii(span, o, 20),
                BagIndex = BinaryHelpers.ReadUInt16LE(span, o + 20),
            };
        }
        return result;
    }

    public static SampleHeader[] ParseShdr(ReadOnlyMemory<byte> chunk)
    {
        var span = chunk.Span;
        var count = span.Length / 46;
        var result = new SampleHeader[count];
        for (var i = 0; i < count; i++)
        {
            var o = i * 46;
            result[i] = new SampleHeader
            {
                Name = BinaryHelpers.ReadFixedAscii(span, o, 20),
                Start = BinaryHelpers.ReadUInt32LE(span, o + 20),
                End = BinaryHelpers.ReadUInt32LE(span, o + 24),
                StartLoop = BinaryHelpers.ReadUInt32LE(span, o + 28),
                EndLoop = BinaryHelpers.ReadUInt32LE(span, o + 32),
                SampleRate = BinaryHelpers.ReadUInt32LE(span, o + 36),
                OriginalPitch = span[o + 40],
                PitchCorrection = (sbyte)span[o + 41],
                SampleLink = BinaryHelpers.ReadUInt16LE(span, o + 42),
                SampleType = (SFSampleLink)BinaryHelpers.ReadUInt16LE(span, o + 44),
            };
        }
        return result;
    }
}
