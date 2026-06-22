using System.Globalization;

namespace MidiSharp.Loader.Sfz;

/// <summary>
/// Parses SFZ key-opcode values, which may be either a raw MIDI note number
/// (<c>60</c>) or a note name (<c>c4</c>, <c>c#4</c>, <c>db5</c>, <c>a-1</c>).
/// </summary>
/// <remarks>
/// Note-name octave convention follows the sfzformat.com default in which
/// middle C (MIDI 60) is <c>c4</c>, i.e. <c>midi = (octave + 1) * 12 +
/// pitchClass</c>. Banks that authored against a different octave convention
/// compensate with <c>octave_offset</c> / <c>note_offset</c> in
/// <see cref="SfzControl"/>, which the translator applies on top of this.
/// </remarks>
internal static class SfzNoteNames
{
    /// <summary>
    /// Parse a key value to a MIDI note (no control offset applied). Returns
    /// false if the token is neither a number nor a recognizable note name.
    /// </summary>
    public static bool TryParse(string value, out int midi)
    {
        midi = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();

        // Plain integer.
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out midi))
            return true;

        // Note name: letter, optional accidental, signed octave.
        var i = 0;
        int pc = (char.ToLowerInvariant(value[i]) switch
        {
            'c' => 0, 'd' => 2, 'e' => 4, 'f' => 5, 'g' => 7, 'a' => 9, 'b' => 11,
            _ => -1,
        });
        if (pc < 0) return false;
        i++;

        if (i < value.Length && (value[i] == '#' || value[i] == 's')) { pc++; i++; }
        else if (i < value.Length && (value[i] == 'b' || value[i] == 'f')) { pc--; i++; }

        if (i >= value.Length) return false;
        if (!int.TryParse(value.Substring(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int octave))
            return false;

        midi = (octave + 1) * 12 + pc;
        return midi is >= 0 and <= 127;
    }
}
