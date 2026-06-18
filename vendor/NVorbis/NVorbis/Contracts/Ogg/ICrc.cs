namespace MidiSharp.Audio.Vorbis.Contracts.Ogg
{
    interface ICrc
    {
        void Reset();
        void Update(int nextVal);
        bool Test(uint checkCrc);
    }
}
