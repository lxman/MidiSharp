namespace MidiSharp.Hosting;

/// <summary>
/// A format-neutral, sample-accurate event handed to a plugin for the current block — the union of
/// VST's <c>deltaFrames</c>/<c>VstMidiEvent</c> and CLAP's <c>clap_event_*</c>. <see cref="SampleOffset"/>
/// is the position within the block where the event takes effect, so instrument hosting and parameter
/// automation stay sample-accurate (the synth already produces timestamped events to feed this).
/// </summary>
/// <param name="SampleOffset">Frames from the start of the current block (0..blockFrames-1).</param>
/// <param name="Status">MIDI status byte (e.g. 0x90 note-on, 0x80 note-off, 0xB0 control change).</param>
/// <param name="Data1">First MIDI data byte (note number / controller number).</param>
/// <param name="Data2">Second MIDI data byte (velocity / controller value).</param>
public readonly record struct HostEvent(int SampleOffset, byte Status, byte Data1, byte Data2);
