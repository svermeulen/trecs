using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Trecs.Collections;

namespace Trecs.Internal
{
    /// <summary>
    /// Reads and writes <see cref="RecordingBundle"/> instances to streams or
    /// files. Reuses an internal <see cref="SerializationBuffer"/> across calls
    /// to avoid allocations when the same serializer handles many save/load
    /// cycles (e.g. the recorder UI's save library).
    ///
    /// Main-thread only.
    /// </summary>
    public sealed class RecordingBundleSerializer : IDisposable
    {
        static readonly TrecsLog _log = TrecsLog.Default;

        readonly SerializationBuffer _buffer;
        bool _disposed;

        public RecordingBundleSerializer(SerializerRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            _buffer = new SerializationBuffer(registry);
        }

        /// <summary>
        /// Write <paramref name="bundle"/> to <paramref name="stream"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="bundle"/> or <paramref name="stream"/> is null, or any required bundle field is null.</exception>
        /// <exception cref="ObjectDisposedException">The serializer has been disposed.</exception>
        public void Save(RecordingBundle bundle, Stream stream)
        {
            ThrowIfDisposed();
            if (bundle == null)
                throw new ArgumentNullException(nameof(bundle));
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            ValidateBundleForSave(bundle);

            long totalBytes;
            try
            {
                _buffer.ClearMemoryStream();
                _buffer.StartWrite(version: bundle.Header.Version, includeTypeChecks: true);

                _buffer.Write("Header", bundle.Header);
                WriteMemoryBytes("initialSnapshot", bundle.InitialSnapshot);
                WriteMemoryBytes("inputQueue", bundle.InputQueue);
                _buffer.Write("Checksums", bundle.Checksums);

                WriteSnapshotList("Anchor", bundle.Anchors, writeLabel: false);
                WriteSnapshotList("Bookmark", bundle.Bookmarks, writeLabel: true);

                _buffer.Write<int>("BundleSentinel", TrecsConstants.RecordingSentinelValue);
                totalBytes = _buffer.EndWrite();
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }

            _buffer.MemoryStream.Position = 0;
            _buffer.MemoryStream.CopyTo(stream);

            _log.Debug(
                "Bundle serialized ({0:0.00} kb): {1} anchors, {2} bookmarks",
                totalBytes / 1024f,
                bundle.Anchors.Count,
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
            ThrowIfDisposed();
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
        /// <exception cref="ObjectDisposedException">The serializer has been disposed.</exception>
        public RecordingBundle Load(Stream stream)
        {
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            try
            {
                LoadStreamIntoBuffer(stream);
                _buffer.StartRead();

                var header = _buffer.Read<BundleHeader>("Header");
                var initialSnapshot = ReadByteArray("initialSnapshot");
                var inputQueue = ReadByteArray("inputQueue");
                var checksums = _buffer.Read<IterableDictionary<int, ulong>>("checksums");

                var anchors = ReadSnapshotList("Anchor", SnapshotKind.Anchor, readLabel: false);
                var bookmarks = ReadSnapshotList(
                    "Bookmark",
                    SnapshotKind.Bookmark,
                    readLabel: true
                );

                var sentinel = _buffer.Read<int>("BundleSentinel");
                if (sentinel != TrecsConstants.RecordingSentinelValue)
                {
                    // Bundle-level sentinel (an int) is distinct from the
                    // Layer-1 EndOfPayloadMarker (a byte) verified by the
                    // StopRead call below: this one catches bundle-format
                    // drift where the trailing payload marker is intact
                    // but the bundle's own structure has been corrupted
                    // (e.g. truncated anchors/snapshots list mid-write).
                    // Release-strict — must fire in release builds too.
                    throw new SerializationException(
                        $"Bundle sentinel mismatch: expected "
                            + $"{TrecsConstants.RecordingSentinelValue}, got {sentinel}. "
                            + "Bundle is truncated or corrupt."
                    );
                }

                _buffer.StopRead(verifySentinel: true);

                return new RecordingBundle
                {
                    Header = header,
                    InitialSnapshot = initialSnapshot,
                    InputQueue = inputQueue,
                    Checksums = checksums,
                    Anchors = anchors,
                    Bookmarks = bookmarks,
                };
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
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
            ThrowIfDisposed();
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
            ThrowIfDisposed();
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            try
            {
                LoadStreamIntoBuffer(stream);
                _buffer.StartRead();
                var header = _buffer.Read<BundleHeader>("Header");
                // Header sits at the start of the payload, so the rest of the
                // payload (sentinel included) is unread on purpose. Skip
                // sentinel verification for the same reason
                // SnapshotSerializer.PeekMetadata does.
                _buffer.StopRead(verifySentinel: false);
                return header;
            }
            catch
            {
                _buffer.ResetForErrorRecovery();
                throw;
            }
        }

        /// <summary>
        /// Read just the bundle header from <paramref name="filePath"/>.
        /// </summary>
        public BundleHeader PeekHeader(string filePath)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Bundle file not found", filePath);

            using var fs = File.OpenRead(filePath);
            return PeekHeader(fs);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _buffer.Dispose();
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
            if (bundle.Anchors == null)
                throw new ArgumentNullException(nameof(bundle) + ".Anchors");
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
            var length = _buffer.ReadBytes(name, ref buffer);
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
                _buffer.WriteBytes(name, Array.Empty<byte>(), 0, 0);
                return;
            }
            if (!MemoryMarshal.TryGetArray(payload, out var seg))
            {
                throw new InvalidOperationException(
                    $"Cannot serialize '{name}': payload is backed by non-array memory."
                );
            }
            _buffer.WriteBytes(name, seg.Array, seg.Offset, seg.Count);
        }

        void WriteSnapshotList(
            string namePrefix,
            IReadOnlyList<WorldSnapshot> list,
            bool writeLabel
        )
        {
            _buffer.Write($"{namePrefix}Count", list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                _buffer.Write($"{namePrefix}Frame", entry.FixedFrame);
                if (writeLabel)
                {
                    _buffer.WriteString($"{namePrefix}Label", entry.Label);
                }
                WriteMemoryBytes($"{namePrefix.ToLowerInvariant()}Payload", entry.Payload);
            }
        }

        List<WorldSnapshot> ReadSnapshotList(string namePrefix, SnapshotKind kind, bool readLabel)
        {
            var count = _buffer.Read<int>($"{namePrefix}Count");
            var result = new List<WorldSnapshot>(count);
            for (int i = 0; i < count; i++)
            {
                var frame = _buffer.Read<int>($"{namePrefix}Frame");
                var label = readLabel ? _buffer.ReadString($"{namePrefix}Label") : "";
                var payload = ReadByteArray($"{namePrefix.ToLowerInvariant()}Payload");
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

        void LoadStreamIntoBuffer(Stream stream)
        {
            _buffer.ClearMemoryStream();
            stream.CopyTo(_buffer.MemoryStream);
            if (_buffer.MemoryStream.Length == 0)
            {
                throw new SerializationException(
                    "Bundle stream is empty — cannot load an empty bundle."
                );
            }
            _buffer.MemoryStream.Position = 0;
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RecordingBundleSerializer));
            }
        }
    }
}
