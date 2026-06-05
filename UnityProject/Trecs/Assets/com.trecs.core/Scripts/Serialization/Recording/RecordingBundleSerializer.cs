using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Reads and writes <see cref="RecordingBundle"/> instances to streams or
    /// files. Reuses an internal write buffer (<see cref="SerializationData"/>) and read buffer
    /// (<see cref="SerializationReadBuffer"/>) across calls to avoid allocations when the same
    /// serializer handles many save/load cycles (e.g. the recorder UI's save library).
    ///
    /// Main-thread only.
    /// </summary>
    public sealed class RecordingBundleSerializer
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        readonly SerializationHelper _helper;
        readonly SerializationData _writeData = new(); // multi-field write scratch; emitted via WriteContiguousTo
        readonly SerializationReadBuffer _readBuffer = new(); // drains + views a stream for multi-field read

        public RecordingBundleSerializer(SerializerRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            _helper = new SerializationHelper(registry);
        }

        /// <summary>
        /// Write <paramref name="bundle"/> to <paramref name="stream"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bundle"/> or <paramref name="stream"/> is null, or any required bundle field is null.</exception>
        public void Save(RecordingBundle bundle, Stream stream)
        {
            if (bundle == null)
                throw new ArgumentNullException(nameof(bundle));
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            ValidateBundleForSave(bundle);

            int totalBytes;
            try
            {
                var writer = _helper.Writer;
                writer.Start(_writeData, version: bundle.Header.Version, includeTypeChecks: true);

                writer.Write("Header", bundle.Header);
                WriteMemoryBytes("initialSnapshot", bundle.InitialSnapshot);
                WriteMemoryBytes("inputQueue", bundle.InputQueue);
                writer.Write("Checksums", bundle.Checksums);

                WriteSnapshotList("Keyframe", bundle.Keyframes, writeLabel: false);
                WriteSnapshotList("Bookmark", bundle.Bookmarks, writeLabel: true);

                writer.Write<int>("BundleSentinel", TrecsConstants.RecordingSentinelValue);
                writer.Complete();
                totalBytes = _writeData.ContiguousSize;
            }
            catch
            {
                _helper.ResetForErrorRecovery();
                throw;
            }

            // Emit the contiguous form straight to the destination — no intermediate MemoryStream.
            _writeData.WriteContiguousTo(stream);

            _log.Debug(
                "Bundle serialized ({0:0.00} kb): {1} keyframes, {2} bookmarks",
                totalBytes / 1024f,
                bundle.Keyframes.Count,
                bundle.Bookmarks.Count
            );
        }

        /// <summary>
        /// Write <paramref name="bundle"/> to <paramref name="filePath"/>.
        /// Creates the parent directory if needed; overwrites any existing file.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        public void Save(RecordingBundle bundle, string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = File.Create(filePath);
            Save(bundle, fs);
        }

        /// <summary>
        /// Read a bundle from <paramref name="stream"/>. Verifies the
        /// end-of-payload sentinel; truncated streams throw.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="SerializationException">The stream is empty/truncated or the binary payload is invalid.</exception>
        public RecordingBundle Load(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            try
            {
                var reader = _helper.Reader;
                reader.Start(_readBuffer.Load(stream));

                var header = reader.Read<BundleHeader>("Header");
                var initialSnapshot = ReadByteArray("initialSnapshot");
                var inputQueue = ReadByteArray("inputQueue");
                // Field names are diagnostic labels only (not validated wire data), but keep
                // them matching the Save side to avoid implying otherwise.
                var checksums = reader.Read<IterableDictionary<int, ulong>>("Checksums");

                var keyframes = ReadSnapshotList(
                    "Keyframe",
                    SnapshotKind.Keyframe,
                    readLabel: false
                );
                var bookmarks = ReadSnapshotList(
                    "Bookmark",
                    SnapshotKind.Bookmark,
                    readLabel: true
                );

                var sentinel = reader.Read<int>("BundleSentinel");
                if (sentinel != TrecsConstants.RecordingSentinelValue)
                {
                    // Bundle-level sentinel (an int field inside the payload) is distinct from the
                    // Layer-1 full-consumption check StopRead performs below: this one catches
                    // bundle-format drift where the payload framing is intact but the bundle's own
                    // structure has been corrupted (e.g. truncated keyframes/snapshots list
                    // mid-write). Release-strict — must fire in release builds too.
                    throw new SerializationException(
                        $"Bundle sentinel mismatch: expected "
                            + $"{TrecsConstants.RecordingSentinelValue}, got {sentinel}. "
                            + "Bundle is truncated or corrupt."
                    );
                }

                reader.Complete();

                return new RecordingBundle
                {
                    Header = header,
                    InitialSnapshot = initialSnapshot,
                    InputQueue = inputQueue,
                    Checksums = checksums,
                    Keyframes = keyframes,
                    Bookmarks = bookmarks,
                };
            }
            catch
            {
                _helper.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Read a bundle from <paramref name="filePath"/>.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="filePath"/> is null or empty.</exception>
        /// <exception cref="FileNotFoundException">No file at <paramref name="filePath"/>.</exception>
        public RecordingBundle Load(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Bundle file not found", filePath);

            using var fs = File.OpenRead(filePath);
            return Load(fs);
        }

        /// <summary>
        /// Read just the bundle header from <paramref name="stream"/> without
        /// parsing snapshots, inputs, or checksums. Useful for save-library UIs
        /// that need to display frame range and version.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        /// <exception cref="SerializationException">The stream is empty/truncated or the binary payload is invalid.</exception>
        public BundleHeader PeekHeader(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            try
            {
                var reader = _helper.Reader;
                reader.Start(_readBuffer.Load(stream));
                var header = reader.Read<BundleHeader>("Header");
                // Header sits at the start of the payload, so the rest is unread on purpose. Skip
                // the full-consumption check, same as SnapshotSerializer.PeekMetadata.
                reader.CompletePartial();
                return header;
            }
            catch
            {
                _helper.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Read just the bundle header from <paramref name="filePath"/>.
        /// </summary>
        public BundleHeader PeekHeader(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Bundle file not found", filePath);

            using var fs = File.OpenRead(filePath);
            return PeekHeader(fs);
        }

        static void ValidateBundleForSave(RecordingBundle bundle)
        {
            if (bundle.Header == null)
                throw new ArgumentNullException(nameof(bundle) + ".Header");
            if (bundle.InitialSnapshot.IsEmpty)
                throw new ArgumentException(
                    "InitialSnapshot must be a non-empty payload",
                    nameof(bundle)
                );
            if (bundle.Checksums == null)
                throw new ArgumentNullException(nameof(bundle) + ".Checksums");
            if (bundle.Keyframes == null)
                throw new ArgumentNullException(nameof(bundle) + ".Keyframes");
            if (bundle.Bookmarks == null)
                throw new ArgumentNullException(nameof(bundle) + ".Bookmarks");
        }

        // Read a length-prefixed byte[] payload, returning an exact-length
        // ReadOnlyMemory<byte>. ReadBytes can hand back a buffer larger than
        // the actual payload when reusing pooled storage, so we slice to
        // exact length before wrapping.
        ReadOnlyMemory<byte> ReadByteArray(string name)
        {
            byte[] buffer = null;
            var length = _helper.Reader.ReadBytes(name, ref buffer);
            if (buffer == null || length == 0)
            {
                return ReadOnlyMemory<byte>.Empty;
            }
            return new ReadOnlyMemory<byte>(buffer, 0, length);
        }

        // Length-prefixed write of a ReadOnlyMemory payload. The underlying
        // ISerializationWriter API is byte[]+offset+count; we extract the
        // backing array via MemoryMarshal so pooled / oversized buffers
        // don't trigger an intermediate copy.
        void WriteMemoryBytes(string name, ReadOnlyMemory<byte> payload)
        {
            if (payload.IsEmpty)
            {
                _helper.Writer.WriteBytes(name, Array.Empty<byte>(), 0, 0);
                return;
            }
            if (!MemoryMarshal.TryGetArray(payload, out var seg))
            {
                throw new InvalidOperationException(
                    $"Cannot serialize '{name}': payload is backed by non-array memory."
                );
            }
            _helper.Writer.WriteBytes(name, seg.Array, seg.Offset, seg.Count);
        }

        void WriteSnapshotList(
            string namePrefix,
            IReadOnlyList<WorldSnapshot> list,
            bool writeLabel
        )
        {
            var writer = _helper.Writer;
            var payloadName = $"{namePrefix.ToLowerInvariant()}Payload";
            writer.Write($"{namePrefix}Count", list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                writer.Write($"{namePrefix}Frame", entry.FixedFrame);
                if (writeLabel)
                {
                    writer.WriteString($"{namePrefix}Label", entry.Label);
                }
                WriteMemoryBytes(payloadName, entry.Payload);
            }
        }

        List<WorldSnapshot> ReadSnapshotList(string namePrefix, SnapshotKind kind, bool readLabel)
        {
            var reader = _helper.Reader;
            var payloadName = $"{namePrefix.ToLowerInvariant()}Payload";
            var count = reader.Read<int>($"{namePrefix}Count");
            var result = new List<WorldSnapshot>(count);
            for (int i = 0; i < count; i++)
            {
                var frame = reader.Read<int>($"{namePrefix}Frame");
                var label = readLabel ? reader.ReadString($"{namePrefix}Label") : "";
                var payload = ReadByteArray(payloadName);
                result.Add(
                    new WorldSnapshot
                    {
                        FixedFrame = frame,
                        Kind = kind,
                        Label = label,
                        Payload = payload,
                    }
                );
            }
            return result;
        }
    }
}
