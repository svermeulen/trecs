using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Unity.Collections;

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
    /// Two independent layers, both keyed by stable system index at runtime:
    /// <list type="bullet">
    /// <item>Channels (<see cref="EnableChannel"/>): non-deterministic, ephemeral, not serialized.</item>
    /// <item>Paused: deterministic, included in world snapshot/recording state.
    /// Serialized sparse and by system <i>identity</i> (64-bit hash of the
    /// debug name + same-name ordinal) rather than by index, so adding/
    /// removing/reordering systems does not invalidate snapshots — a pause
    /// follows its system, and a pause for a system that no longer exists is
    /// dropped with a warning.</item>
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

        // Identity tables for serialization (see class docs): the wire format
        // identifies each paused system by a 64-bit xxHash of its debug name
        // (not the full string — fixed 12 bytes per entry and no string
        // allocation on load) so restores survive system list changes.
        // _systemNameOrdinals[i] is system i's occurrence index among systems
        // sharing its name hash (registration order), disambiguating
        // duplicate registrations of the same system type.
        TrecsLog _log;
        ulong[] _systemNameHashes;
        int[] _systemNameOrdinals;
        Dictionary<ulong, List<int>> _systemIndicesByNameHash;

        public void Initialize(IReadOnlyList<SystemEntry> systems, TrecsLog log)
        {
            TrecsDebugAssert.That(!_isInitialized);
            TrecsDebugAssert.That(!_isDisposed);
            TrecsDebugAssert.IsNotNull(systems);
            TrecsDebugAssert.IsNotNull(log);

            _log = log;
            var systemCount = systems.Count;
            _systemCount = systemCount;
            _channelMasks = new int[systemCount];

            _systemNameHashes = new ulong[systemCount];
            _systemNameOrdinals = new int[systemCount];
            _systemIndicesByNameHash = new Dictionary<ulong, List<int>>(systemCount);
            for (int i = 0; i < systemCount; i++)
            {
                // Name-derived and stable across sessions/platforms.
                var nameHash = CollisionResistantHashCalculator.ComputeXxHash64(
                    systems[i].DebugName
                );
                if (!_systemIndicesByNameHash.TryGetValue(nameHash, out var indices))
                {
                    indices = new List<int>(1);
                    _systemIndicesByNameHash.Add(nameHash, indices);
                }
                else
                {
                    // A hash match is expected to mean "same name, later
                    // occurrence". A cross-name collision (~2^-64 per pair)
                    // would silently merge two systems' ordinal spaces and
                    // let a restored pause land on the wrong system — catch
                    // it in debug builds.
                    TrecsDebugAssert.That(
                        systems[indices[0]].DebugName == systems[i].DebugName,
                        "xxHash64 collision between system debug names {0} and {1}",
                        systems[indices[0]].DebugName,
                        systems[i].DebugName
                    );
                }
                _systemNameOrdinals[i] = indices.Count;
                indices.Add(i);
                _systemNameHashes[i] = nameHash;
            }

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
            TrecsDebugAssert.That(!_isDisposed);

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
                TrecsDebugAssert.That(_isInitialized);
                return _systemCount;
            }
        }

        public bool IsSystemEnabled(int systemIndex, EnableChannel channel)
        {
            TrecsDebugAssert.That(_isInitialized);
            AssertValidIndex(systemIndex);
            return (_channelMasks[systemIndex] & (int)channel) == 0;
        }

        public void SetSystemEnabled(int systemIndex, EnableChannel channel, bool enabled)
        {
            TrecsDebugAssert.That(_isInitialized);
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
            TrecsDebugAssert.That(_isInitialized);
            AssertValidIndex(systemIndex);

            var wordIndex = systemIndex / BitsPerWord;
            var bitIndex = systemIndex & WordMask;
            return (_pausedWords[wordIndex] & (1ul << bitIndex)) != 0;
        }

        public void SetSystemPaused(int systemIndex, bool paused)
        {
            TrecsDebugAssert.That(_isInitialized);
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
            TrecsDebugAssert.That(_isInitialized);
            AssertValidIndex(systemIndex);
            return !ShouldSkipSystem(systemIndex);
        }

        public void Serialize(ISerializationWriter writer)
        {
            TrecsDebugAssert.That(_isInitialized);

            // Channels are intentionally NOT serialized — they're ephemeral
            // and reapplied by their respective owners (BundleReplayer for
            // EnableChannel.Playback, the editor for EnableChannel.Editor,
            // and application code for EnableChannel.User) on each session.
            //
            // Paused state is written sparse, by identity (name hash +
            // same-name ordinal), in ascending system index order
            // (deterministic — same state always produces the same bytes).
            // The common case is zero paused systems, so this is typically a
            // single int — cheaper than the index-keyed bit-blit it replaced,
            // and it keeps snapshots loadable across system
            // add/remove/reorder.
            int pausedCount = 0;
            for (int w = 0; w < _wordCount; w++)
            {
                var word = _pausedWords[w];
                while (word != 0)
                {
                    word &= word - 1;
                    pausedCount++;
                }
            }

            writer.Write("PausedCount", pausedCount);

            if (pausedCount == 0)
            {
                return;
            }

            for (int w = 0; w < _wordCount; w++)
            {
                var word = _pausedWords[w];
                if (word == 0)
                {
                    continue;
                }
                int baseIndex = w * BitsPerWord;
                for (int b = 0; b < BitsPerWord; b++)
                {
                    if ((word & (1ul << b)) == 0)
                    {
                        continue;
                    }
                    int systemIndex = baseIndex + b;
                    writer.Write("NameHash", _systemNameHashes[systemIndex]);
                    writer.Write("Ordinal", _systemNameOrdinals[systemIndex]);
                }
            }
        }

        public void Deserialize(ISerializationReader reader)
        {
            TrecsDebugAssert.That(_isInitialized);

            for (int w = 0; w < _wordCount; w++)
            {
                _pausedWords[w] = 0;
            }

            var pausedCount = reader.Read<int>("PausedCount");
            TrecsDebugAssert.That(pausedCount >= 0);

            for (int i = 0; i < pausedCount; i++)
            {
                var nameHash = reader.Read<ulong>("NameHash");
                var ordinal = reader.Read<int>("Ordinal");

                if (
                    _systemIndicesByNameHash.TryGetValue(nameHash, out var indices)
                    && ordinal < indices.Count
                )
                {
                    SetSystemPaused(indices[ordinal], true);
                }
                else
                {
                    // The snapshot paused a system this world doesn't have
                    // (removed or renamed since save). Dropping the pause is
                    // the only sensible restore; warn so the divergence from
                    // the saved state isn't silent. Only the name hash is
                    // available — the wire deliberately doesn't carry the
                    // name string (see the identity-table comment above).
                    _log.Warning(
                        "Snapshot pauses a system (debug-name hash {0:X16}, occurrence {1}) "
                            + "which does not exist in this world — dropping that pause.",
                        nameHash,
                        ordinal
                    );
                }
            }
        }

        void AssertValidIndex(int systemIndex)
        {
            TrecsDebugAssert.That(
                systemIndex >= 0 && systemIndex < _systemCount,
                "System index {0} out of range [0, {1})",
                systemIndex,
                _systemCount
            );
        }
    }
}
