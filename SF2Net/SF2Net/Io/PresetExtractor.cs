namespace SF2Net.Io;

/// <summary>
/// Repackages a single preset and its dependencies into a standalone SF2 byte array.
/// Port of <c>SFont::extract()</c> from sflib.
/// </summary>
internal sealed class PresetExtractor
{
    private readonly SoundFont _sf;
    private readonly SdtaChunkReader _sdta;

    public PresetExtractor(SoundFont sf, SdtaChunkReader sdta)
    {
        _sf = sf;
        _sdta = sdta;
    }

    public byte[] Extract(Preset preset)
    {
        var smpl = new MemoryStream();
        var shdr = new MemoryStream();
        var igen = new MemoryStream();
        var imod = new MemoryStream();
        var ibag = new MemoryStream();
        var inst = new MemoryStream();
        var pgen = new MemoryStream();
        var pmod = new MemoryStream();
        var pbag = new MemoryStream();
        var phdr = new MemoryStream();

        // Find which instruments this preset references.
        var instRefs = new HashSet<int>();
        foreach (var z in preset.Zones)
            if (z.InstrumentIndex >= 0) instRefs.Add(z.InstrumentIndex);

        // Build inst/ibag/igen/imod/shdr/smpl chunks by walking each referenced instrument.
        foreach (var instrument in _sf.Instruments)
        {
            if (!instRefs.Contains(instrument.Index)) continue;

            var instRecord = new InstrumentRecord
            {
                Name = instrument.Name,
                BagIndex = (ushort)(ibag.Length / 4),
            };

            // Dedupe & emit samples referenced by this instrument.
            var samplesReferenced = new List<ExcisedSample>();
            foreach (var z in instrument.Zones)
            {
                if (z.Sample is null) continue;
                var newHeader = CloneAndRebaseHeader(z.Sample);

                // Try to find a previously emitted identical sample.
                ExcisedSample? match = null;
                foreach (var es in samplesReferenced)
                {
                    if (HeadersEquivalent(newHeader, es.Header))
                    {
                        match = es;
                        break;
                    }
                }
                if (match is not null)
                {
                    z.ExcisedSample = match;
                    continue;
                }

                var raw = _sdta.GetRawBytes(z.Sample.Start, z.Sample.End).ToArray();

                if (raw.Length == 0) return [];

                // Pad with trailing zeros so there are at least 93 bytes of silence after the loop end.
                var zeroCount = 0;
                var idx = raw.Length - 1;
                while (idx >= 0 && raw[idx] == 0) { zeroCount++; idx--; }
                if (zeroCount < 93)
                {
                    var extra = 93 - zeroCount;
                    Array.Resize(ref raw, raw.Length + extra);
                }

                var newSample = new ExcisedSample { Data = raw, Header = newHeader };

                // SF2 §6.1 validity constraints.
                if (newHeader.End - newHeader.Start < 48) return [];
                if (newHeader.End - newHeader.EndLoop < 8)
                    newHeader.EndLoop = newHeader.End - 8;
                if (newHeader.StartLoop - newHeader.Start < 8)
                    newHeader.StartLoop = newHeader.Start + 8;
                if (newHeader.EndLoop - newHeader.StartLoop < 32) return [];

                z.ExcisedSample = newSample;
                samplesReferenced.Add(newSample);
            }

            // Append sample data, rebase headers to their new positions in smpl, write shdr entries.
            foreach (var es in samplesReferenced)
            {
                var offset = (uint)(smpl.Length / 2);
                smpl.Write(es.Data, 0, es.Data.Length);
                es.Header.Start += offset;
                es.Header.End += offset;
                es.Header.StartLoop += offset;
                es.Header.EndLoop += offset;

                // Stereo link fix-up: try to find the linked partner among samples we just wrote.
                if (IsStereo(es.Header.SampleType))
                {
                    var originalLink = es.Header.SampleLink;
                    var foundLink = false;
                    foreach (var other in samplesReferenced)
                    {
                        if (originalLink == other.Header.OriginalIndex)
                        {
                            es.Header.SampleLink = (ushort)(shdr.Length / 46);
                            foundLink = true;
                        }
                    }
                    if (!foundLink)
                    {
                        es.Header.SampleType = es.Header.SampleType switch
                        {
                            SFSampleLink.RightSample or SFSampleLink.LeftSample => SFSampleLink.MonoSample,
                            SFSampleLink.RomRightSample or SFSampleLink.RomLeftSample => SFSampleLink.RomMonoSample,
                            _ => es.Header.SampleType,
                        };
                        if (!foundLink) es.Header.SampleLink = 0;
                    }
                }

                es.Header.Index = (uint)(shdr.Length / 46);
                Span<byte> shdrBuf = stackalloc byte[46];
                Assemblers.WriteShdr(shdrBuf, es.Header);
                shdr.Write(shdrBuf.ToArray(), 0, 46);
            }

            // Emit ibag/igen/imod entries for each zone in the instrument.
            foreach (var z in instrument.Zones)
            {
                var bag = new BagRecord
                {
                    GenIndex = (ushort)(igen.Length / 4),
                    ModIndex = (ushort)(imod.Length / 10),
                };

                // Append the sampleID generator pointing at the (deduped, re-indexed) sample.
                if (z.Sample is not null && z.ExcisedSample is not null)
                {
                    z.Generators.Add(new Generator(SFGenerator.SampleID,
                        new GeneratorAmount((ushort)z.ExcisedSample.Header.Index)));
                }

                var n = Math.Max(z.Generators.Count, z.Modulators.Count);
                for (var i = 0; i < n; i++)
                {
                    if (i < z.Generators.Count)
                    {
                        Span<byte> b = stackalloc byte[4];
                        Assemblers.WriteGen(b, z.Generators[i]);
                        igen.Write(b.ToArray(), 0, 4);
                    }
                    if (i < z.Modulators.Count)
                    {
                        Span<byte> b = stackalloc byte[10];
                        Assemblers.WriteMod(b, z.Modulators[i]);
                        imod.Write(b.ToArray(), 0, 10);
                    }
                }

                Span<byte> bagBuf = stackalloc byte[4];
                Assemblers.WriteBag(bagBuf, bag);
                ibag.Write(bagBuf.ToArray(), 0, 4);
            }

            // Update preset zone InstrumentIndex values to point at this instrument's new index.
            var newInstIdx = (int)(inst.Length / 22);
            foreach (var pz in preset.Zones)
                if (pz.InstrumentIndex == instrument.Index)
                    pz.InstrumentIndex = newInstIdx;

            Span<byte> instBuf = stackalloc byte[22];
            Assemblers.WriteInst(instBuf, instRecord);
            inst.Write(instBuf.ToArray(), 0, 22);
        }

        // Now build pgen/pmod/pbag with this preset's zones, re-adding the instrument generator at the end.
        foreach (var pz in preset.Zones)
        {
            if (pz.InstrumentIndex >= 0)
                pz.Generators.Add(new Generator(SFGenerator.Instrument,
                    new GeneratorAmount((ushort)pz.InstrumentIndex)));
        }

        foreach (var pz in preset.Zones)
        {
            var bag = new BagRecord
            {
                GenIndex = (ushort)(pgen.Length / 4),
                ModIndex = (ushort)(pmod.Length / 10),
            };
            var n = Math.Max(pz.Generators.Count, pz.Modulators.Count);
            for (var i = 0; i < n; i++)
            {
                if (i < pz.Generators.Count)
                {
                    Span<byte> b = stackalloc byte[4];
                    Assemblers.WriteGen(b, pz.Generators[i]);
                    pgen.Write(b.ToArray(), 0, 4);
                }
                if (i < pz.Modulators.Count)
                {
                    Span<byte> b = stackalloc byte[10];
                    Assemblers.WriteMod(b, pz.Modulators[i]);
                    pmod.Write(b.ToArray(), 0, 10);
                }
            }
            Span<byte> bb = stackalloc byte[4];
            Assemblers.WriteBag(bb, bag);
            pbag.Write(bb.ToArray(), 0, 4);
        }

        // Preset header for this preset.
        Span<byte> phdrBuf = stackalloc byte[38];
        Assemblers.WritePhdr(phdrBuf, new PresetHeaderRecord
        {
            Name = preset.Name,
            Preset = preset.Number,
            Bank = preset.Bank,
            BagIndex = 0,
            Library = preset.Library,
            Genre = preset.Genre,
            Morphology = preset.Morphology,
        });
        phdr.Write(phdrBuf.ToArray(), 0, 38);

        // Terminal EOP header.
        Assemblers.WritePhdr(phdrBuf, new PresetHeaderRecord
        {
            Name = "EOP",
            BagIndex = (ushort)(pbag.Length / 4),
        });
        phdr.Write(phdrBuf.ToArray(), 0, 38);

        // Terminal EOI inst.
        Span<byte> instBuf2 = stackalloc byte[22];
        Assemblers.WriteInst(instBuf2, new InstrumentRecord
        {
            Name = "EOI",
            BagIndex = (ushort)(ibag.Length / 4),
        });
        inst.Write(instBuf2.ToArray(), 0, 22);

        // Terminal bags + zero gen/mod.
        Span<byte> termBag = stackalloc byte[4];
        Assemblers.WriteBag(termBag, new BagRecord { GenIndex = (ushort)(pgen.Length / 4), ModIndex = (ushort)(pmod.Length / 10) });
        pbag.Write(termBag.ToArray(), 0, 4);

        Span<byte> termGen = stackalloc byte[4];
        Assemblers.WriteGen(termGen, new Generator(SFGenerator.StartAddrsOffset, new GeneratorAmount((ushort)0)));
        pgen.Write(termGen.ToArray(), 0, 4);

        Span<byte> termMod = stackalloc byte[10];
        Assemblers.WriteMod(termMod, new Modulator
        {
            SourceOperator = 0,
            DestinationOperator = SFGenerator.StartAddrsOffset,
            Amount = 0,
            AmountSourceOperator = 0,
            TransformOperator = 0,
        });
        pmod.Write(termMod.ToArray(), 0, 10);

        Assemblers.WriteBag(termBag, new BagRecord { GenIndex = (ushort)(igen.Length / 4), ModIndex = (ushort)(imod.Length / 10) });
        ibag.Write(termBag.ToArray(), 0, 4);

        igen.Write(termGen.ToArray(), 0, 4);
        imod.Write(termMod.ToArray(), 0, 10);

        // Terminal EOS sample header (46 bytes).
        Span<byte> shdrBuf2 = stackalloc byte[46];
        var eos = new SampleHeader { Name = "EOS" };
        eos.Start = (uint)(smpl.Length / 2);
        eos.End = eos.Start;
        eos.StartLoop = eos.Start;
        eos.EndLoop = eos.Start;
        Assemblers.WriteShdr(shdrBuf2, eos);
        shdr.Write(shdrBuf2.ToArray(), 0, 46);

        if (smpl.Length == 0)
        {
            // No samples to package — original C++ returned 0 for this case.
            return [];
        }

        // Build sdta LIST.
        if ((smpl.Length & 1) != 0) smpl.WriteByte(0);
        var smplBytes = smpl.ToArray();
        var sdta = WrapList("sdta", [("smpl", smplBytes)]);

        // Build pdta LIST.
        var pdtaParts = new (string, byte[])[]
        {
            ("phdr", PadEven(phdr.ToArray())),
            ("pbag", PadEven(pbag.ToArray())),
            ("pmod", PadEven(pmod.ToArray())),
            ("pgen", PadEven(pgen.ToArray())),
            ("inst", PadEven(inst.ToArray())),
            ("ibag", PadEven(ibag.ToArray())),
            ("imod", PadEven(imod.ToArray())),
            ("igen", PadEven(igen.ToArray())),
            ("shdr", PadEven(shdr.ToArray())),
        };
        var pdta = WrapList("pdta", pdtaParts);

        // Build INFO LIST.
        var infoBody = InfoAssembler.Build(_sf.Info, overrideBankName: preset.Name);
        var info = WrapList("INFO", [((string)null!, infoBody)], infoBodyRaw: true);

        // Final RIFF/sfbk.
        return BuildRiffSfbk(info, sdta, pdta);
    }

    private static SampleHeader CloneAndRebaseHeader(SampleHeader src)
    {
        return new SampleHeader
        {
            Name = src.Name,
            Start = 0,
            End = src.End - src.Start,
            StartLoop = src.StartLoop - src.Start,
            // The C++ code does: dwEnd + dwEndLoop - dwEnd. After rebasing, dwEnd' = end-start,
            // so EndLoop' = (end-start) + (origEndLoop - origEnd).  Equivalent to origEndLoop - start.
            EndLoop = src.EndLoop - src.Start,
            SampleRate = src.SampleRate,
            OriginalPitch = src.OriginalPitch,
            PitchCorrection = src.PitchCorrection,
            SampleType = src.SampleType,
            SampleLink = src.SampleLink,
            OriginalIndex = src.OriginalIndex,
        };
    }

    private static bool HeadersEquivalent(SampleHeader a, SampleHeader b) =>
        a.Start == b.Start && a.End == b.End && a.StartLoop == b.StartLoop && a.EndLoop == b.EndLoop &&
        a.SampleRate == b.SampleRate && a.Name == b.Name && a.OriginalPitch == b.OriginalPitch &&
        a.PitchCorrection == b.PitchCorrection && a.SampleType == b.SampleType &&
        a.SampleLink == b.SampleLink && a.OriginalIndex == b.OriginalIndex;

    private static bool IsStereo(SFSampleLink t) =>
        t is SFSampleLink.RightSample or SFSampleLink.LeftSample
              or SFSampleLink.RomRightSample or SFSampleLink.RomLeftSample;

    private static byte[] PadEven(byte[] data)
    {
        if ((data.Length & 1) == 0) return data;
        var padded = new byte[data.Length + 1];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        return padded;
    }

    /// <summary>
    /// Wraps a set of child chunks into a LIST chunk.
    /// If <paramref name="infoBodyRaw"/> is true, the single child is treated as the LIST's
    /// pre-assembled body (just prefixed with the form-type tag).
    /// </summary>
    private static byte[] WrapList(string formType, (string tag, byte[] body)[] children, bool infoBodyRaw = false)
    {
        using var ms = new MemoryStream();
        // form type
        ms.Write(BinaryHelpers.Ascii.GetBytes(formType), 0, 4);
        if (infoBodyRaw)
        {
            ms.Write(children[0].body, 0, children[0].body.Length);
        }
        else
        {
            foreach ((var tag, var body) in children)
            {
                ms.Write(BinaryHelpers.Ascii.GetBytes(tag), 0, 4);
                Span<byte> size = stackalloc byte[4];
                BinaryHelpers.WriteUInt32LE(size, 0, (uint)body.Length);
                ms.Write(size.ToArray(), 0, 4);
                ms.Write(body, 0, body.Length);
                if ((body.Length & 1) != 0) ms.WriteByte(0);
            }
        }
        var body2 = ms.ToArray();
        var outMs = new MemoryStream();
        outMs.Write(BinaryHelpers.Ascii.GetBytes("LIST"), 0, 4);
        Span<byte> sz = stackalloc byte[4];
        BinaryHelpers.WriteUInt32LE(sz, 0, (uint)body2.Length);
        outMs.Write(sz.ToArray(), 0, 4);
        outMs.Write(body2, 0, body2.Length);
        if ((body2.Length & 1) != 0) outMs.WriteByte(0);
        return outMs.ToArray();
    }

    private static byte[] BuildRiffSfbk(byte[] info, byte[] sdta, byte[] pdta)
    {
        var bodyLen = 4 + info.Length + sdta.Length + pdta.Length; // "sfbk" + 3 LIST chunks
        var ms = new MemoryStream(bodyLen + 8);
        ms.Write(BinaryHelpers.Ascii.GetBytes("RIFF"), 0, 4);
        Span<byte> sz = stackalloc byte[4];
        BinaryHelpers.WriteUInt32LE(sz, 0, (uint)bodyLen);
        ms.Write(sz.ToArray(), 0, 4);
        ms.Write(BinaryHelpers.Ascii.GetBytes("sfbk"), 0, 4);
        ms.Write(info, 0, info.Length);
        ms.Write(sdta, 0, sdta.Length);
        ms.Write(pdta, 0, pdta.Length);
        return ms.ToArray();
    }
}
