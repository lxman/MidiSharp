using System;
using Xunit;

namespace MidiSharp.Dsp.Tests;

/// <summary>Proves the chain runs its members in order, honors bypass, and reflects live edits.</summary>
public sealed class ProcessorChainTests
{
    [Fact]
    public void Empty_chain_is_passthrough()
    {
        var chain = new ProcessorChain();
        float[] buf = new[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var copy = (float[])buf.Clone();
        chain.Process(buf);
        Assert.Equal(copy, buf);
    }

    [Fact]
    public void Members_apply_in_sequence()
    {
        var chain = new ProcessorChain();
        chain.Add(new GainProcessor { GainDb = 6.0206 });   // ×2
        chain.Add(new GainProcessor { GainDb = 6.0206 });   // ×2 again → ×4 total
        var buf = new[] { 0.1f, 0.1f };
        chain.Process(buf);
        Assert.All(buf, v => Assert.True(Math.Abs(v - 0.4f) < 1e-4f, $"expected ~0.4, got {v}"));
    }

    [Fact]
    public void Bypass_leaves_buffer_untouched()
    {
        var chain = new ProcessorChain { Bypass = true };
        chain.Add(new GainProcessor { GainDb = 12 });
        var buf = new[] { 0.1f, 0.1f };
        var copy = (float[])buf.Clone();
        chain.Process(buf);
        Assert.Equal(copy, buf);
    }

    [Fact]
    public void SetAll_replaces_the_whole_chain_in_order()
    {
        var chain = new ProcessorChain();
        chain.Add(new GainProcessor { GainDb = 20 });   // would be ×10 — must be discarded by SetAll
        chain.SetAll([
            new GainProcessor { GainDb = 6.0206 },      // ×2
            new GainProcessor { GainDb = 6.0206 } // ×2  → ×4 total
        ]);
        var buf = new[] { 0.1f, 0.1f };
        chain.Process(buf);
        Assert.All(buf, v => Assert.True(Math.Abs(v - 0.4f) < 1e-4f, $"expected ×4 → ~0.4, got {v}"));

        chain.SetAll([]);   // empty → passthrough
        float[] b2 = new[] { 0.2f, -0.2f };
        var copy = (float[])b2.Clone();
        chain.Process(b2);
        Assert.Equal(copy, b2);
    }

    [Fact]
    public void Remove_takes_effect_on_next_block()
    {
        var chain = new ProcessorChain();
        var g = new GainProcessor { GainDb = 6.0206 };
        chain.Add(g);
        Assert.True(chain.Remove(g));
        var buf = new[] { 0.1f, 0.1f };
        var copy = (float[])buf.Clone();
        chain.Process(buf);
        Assert.Equal(copy, buf);   // chain is empty again
    }
}
