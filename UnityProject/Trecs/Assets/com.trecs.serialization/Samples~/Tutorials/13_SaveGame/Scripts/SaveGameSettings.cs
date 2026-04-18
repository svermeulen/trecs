using System;

namespace Trecs.Samples.SaveGame
{
    [Serializable]
    public class SaveGameSettings
    {
        // Fixed RNG seed. The sample's level layout is hardcoded, so this
        // has no effect on gameplay — included for parity with other
        // deterministic Trecs samples.
        public ulong RandomSeed = 7;
    }
}
