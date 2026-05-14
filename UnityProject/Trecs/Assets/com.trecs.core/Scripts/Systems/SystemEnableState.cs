using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Trecs
{
    /// <summary>
    /// Independent disable channels for non-deterministic system toggles.
    /// A system runs only when no channel has it disabled.
    /// <para>
    /// Channel state is ephemeral: defaults to "all enabled" at world init,
    /// is not part of snapshot/restore, and is not part of recording/playback
    /// state. Use <see cref="WorldAccessor.SetSystemPaused"/> for pauses that
    /// must be deterministic.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Each channel is independently controlled by a different concern:
    /// <list type="bullet">
    /// <item><c>Editor</c> — toggled by the Trecs Hierarchy inspector.</item>
    /// <item><c>Playback</c> — toggled by recording playback to silence input systems.</item>
    /// <item><c>User</c> — toggled by application-side user code (debug menus, kill switches).</item>
    /// </list>
    /// </remarks>
    [Flags]
    public enum EnableChannel
    {
        Editor = 1 << 0,
        Playback = 1 << 1,
        User = 1 << 2,
    }
}

namespace Trecs.Internal
{
    /// <summary>
    /// Owns per-system enable state used by <c>SystemRunner</c> to skip systems.
    /// Two independent layers, both keyed by stable system index:
    /// <list type="bullet">
    /// <item>Channels (<see cref="EnableChannel"/>): non-deterministic, ephemeral, not serialized.</item>
    /// <item>Paused: deterministic, included in world snapshot/recording state.</item>
    /// </list>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class SystemEnableState
    {
        const int BitsPerWord = 64;
        const int WordMask = BitsPerWord - 1;

        // Veto-bit storage: bit set = that channel is *blocking* the system.
        // Default 0 = no channels blocking = system runs. This composes
        // naturally — each channel only writes its own bit, so independent
        // callers (Editor / Playback / User) can never clobber each other's
        // intent. The user-facing API (SetSystemEnabled with `enabled`) hides
        // this polarity: enabled=true clears the bit, enabled=false sets it.
        int[] _channelMasks;
        NativeArray<ulong> _pausedWords;
        int _systemCount;
        int _wordCount;
        bool _isInitialized;
        bool _isDisposed;

        public void Initialize(int systemCount)
        {
            TrecsAssert.That(!_isInitialized);
            TrecsAssert.That(!_isDisposed);
            TrecsAssert.That(systemCount >= 0);

            _systemCount = systemCount;
            _channelMasks = new int[systemCount];

            // Always allocate at least one word so the buffer is valid even
            // for empty worlds.
            _wordCount = Math.Max((systemCount + BitsPerWord - 1) / BitsPerWord, 1);
            _pausedWords = new NativeArray<ulong>(
                _wordCount,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory
            );

            _isInitialized = true;
        }

        public void Dispose()
        {
            TrecsAssert.That(!_isDisposed);

            if (_pausedWords.IsCreated)
            {
                _pausedWords.Dispose();
            }

            _isDisposed = true;
        }

        public int SystemCount
        {
            get
            {
                TrecsAssert.That(_isInitialized);
                return _systemCount;
            }
        }

        public bool IsSystemEnabled(int systemIndex, EnableChannel channel)
        {
            TrecsAssert.That(_isInitialized);
            AssertValidIndex(systemIndex);
            return (_channelMasks[systemIndex] & (int)channel) == 0;
        }

        public void SetSystemEnabled(int systemIndex, EnableChannel channel, bool enabled)
        {
            TrecsAssert.That(_isInitialized);
            AssertValidIndex(systemIndex);

            if (enabled)
            {
                _channelMasks[systemIndex] &= ~(int)channel;
            }
            else
            {
                _channelMasks[systemIndex] |= (int)channel;
            }
        }

        public bool IsSystemPaused(int systemIndex)
        {
            TrecsAssert.That(_isInitialized);
            AssertValidIndex(systemIndex);

            var wordIndex = systemIndex / BitsPerWord;
            var bitIndex = systemIndex & WordMask;
            return (_pausedWords[wordIndex] & (1ul << bitIndex)) != 0;
        }

        public void SetSystemPaused(int systemIndex, bool paused)
        {
            TrecsAssert.That(_isInitialized);
            AssertValidIndex(systemIndex);

            var wordIndex = systemIndex / BitsPerWord;
            var bitIndex = systemIndex & WordMask;
            var mask = 1ul << bitIndex;

            if (paused)
            {
                _pausedWords[wordIndex] |= mask;
            }
            else
            {
                _pausedWords[wordIndex] &= ~mask;
            }
        }

        // Hot path: called once per system per tick from SystemRunner.ExecuteSystem.
        // Returns true if the system should be skipped (any channel disabled OR paused).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ShouldSkipSystem(int systemIndex)
        {
            if (_channelMasks[systemIndex] != 0)
            {
                return true;
            }

            var wordIndex = systemIndex / BitsPerWord;
            var bitIndex = systemIndex & WordMask;
            return (_pausedWords[wordIndex] & (1ul << bitIndex)) != 0;
        }

        // Convenience inverse of ShouldSkipSystem with a bounds-check assert,
        // for debug UIs / tests that want to ask "would this system run right now"
        // without manually combining channel checks and IsSystemPaused.
        public bool IsSystemEffectivelyEnabled(int systemIndex)
        {
            TrecsAssert.That(_isInitialized);
            AssertValidIndex(systemIndex);
            return !ShouldSkipSystem(systemIndex);
        }

        public unsafe void Serialize(ISerializationWriter writer)
        {
            TrecsAssert.That(_isInitialized);

            // Channels are intentionally NOT serialized — they're ephemeral
            // and reapplied by their respective owners (BundlePlayer for
            // EnableChannel.Playback, the editor for EnableChannel.Editor,
            // and application code for EnableChannel.User) on each session.
            writer.Write("SystemCount", _systemCount);
            writer.Write("WordCount", _wordCount);

            if (_wordCount > 0)
            {
                writer.BlitWriteRawBytes(
                    "PausedBits",
                    NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(_pausedWords),
                    _wordCount * sizeof(ulong)
                );
            }
        }

        public unsafe void Deserialize(ISerializationReader reader)
        {
            TrecsAssert.That(_isInitialized);

            var serializedSystemCount = reader.Read<int>("SystemCount");
            var serializedWordCount = reader.Read<int>("WordCount");

            TrecsAssert.IsEqual(serializedSystemCount, _systemCount);
            TrecsAssert.IsEqual(serializedWordCount, _wordCount);

            if (_wordCount > 0)
            {
                reader.BlitReadRawBytes(
                    "PausedBits",
                    NativeArrayUnsafeUtility.GetUnsafePtr(_pausedWords),
                    _wordCount * sizeof(ulong)
                );
            }
        }

        void AssertValidIndex(int systemIndex)
        {
            TrecsAssert.That(
                systemIndex >= 0 && systemIndex < _systemCount,
                "System index {0} out of range [0, {1})",
                systemIndex,
                _systemCount
            );
        }
    }
}
