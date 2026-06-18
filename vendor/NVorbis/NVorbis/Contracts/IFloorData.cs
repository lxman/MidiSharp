namespace MidiSharp.Audio.Vorbis.Contracts
{
    interface IFloorData
    {
        bool ExecuteChannel { get; }
        bool ForceEnergy { get; set; }
        bool ForceNoEnergy { get; set; }
    }
}
