using System;
using System.Collections.Generic;
using System.IO;
using Trecs.Collections;
using Trecs.Internal;

namespace Trecs.Serialization.Internal
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

                _buffer.Write("header", bundle.Header);
                _buffer.WriteBytes(
                    "initialSnapshot",
                    bundle.InitialSnapshot,
                    0,
                    bundle.InitialSnapshot.Length
                );
                _buffer.Write("initialSnapshotChecksum", bundle.InitialSnapshotChecksum);
                _buffer.WriteBytes("inputQueue", bundle.InputQueue, 0, bundle.InputQueue.Length);
                _buffer.Write("checksums", bundle.Checksums);

                _buffer.Write("anchorCount", bundle.Anchors.Count);
                for (int i = 0; i < bundle.Anchors.Count; i++)
                {
                    var anchor = bundle.Anchors[i];
                    _buffer.Write("anchorFrame", anchor.FixedFrame);
                    _buffer.Write("anchorChecksum", anchor.Checksum);
                    _buffer.WriteBytes("anchorPayload", anchor.Payload, 0, anchor.Payload.Length);
                }

                _buffer.Write("snapshotCount", bundle.Snapshots.Count);
                for (int i = 0; i < bundle.Snapshots.Count; i++)
                {
                    var snapshot = bundle.Snapshots[i];
                    _buffer.Write("snapshotFrame", snapshot.FixedFrame);
                    _buffer.Write("snapshotChecksum", snapshot.Checksum);
                    _buffer.WriteString("snapshotLabel", snapshot.Label);
                    _buffer.WriteBytes(
                        "snapshotPayload",
                        snapshot.Payload,
                        0,
                        snapshot.Payload.Length
                    );
                }

                _buffer.Write<int>("bundleSentinel", TrecsConstants.RecordingSentinelValue);
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
                "Bundle serialized ({0.00} kb): {} anchors, {} snapshots",
                totalBytes / 1024f,
                bundle.Anchors.Count,
                bundle.Snapshots.Count
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

                var header = _buffer.Read<BundleHeader>("header");
                var initialSnapshot = ReadByteArray("initialSnapshot");
                var initialSnapshotChecksum = _buffer.Read<uint>("initialSnapshotChecksum");
                var inputQueue = ReadByteArray("inputQueue");
                var checksums = _buffer.Read<DenseDictionary<int, uint>>("checksums");

                var anchorCount = _buffer.Read<int>("anchorCount");
                var anchors = new List<BundleAnchor>(anchorCount);
                for (int i = 0; i < anchorCount; i++)
                {
                    var frame = _buffer.Read<int>("anchorFrame");
                    var checksum = _buffer.Read<uint>("anchorChecksum");
                    anchors.Add(
                        new BundleAnchor
                        {
                            FixedFrame = frame,
                            Checksum = checksum,
                            Payload = ReadByteArray("anchorPayload"),
                        }
                    );
                }

                var snapshotCount = _buffer.Read<int>("snapshotCount");
                var snapshots = new List<BundleSnapshot>(snapshotCount);
                for (int i = 0; i < snapshotCount; i++)
                {
                    var frame = _buffer.Read<int>("snapshotFrame");
                    var checksum = _buffer.Read<uint>("snapshotChecksum");
                    var label = _buffer.ReadString("snapshotLabel");
                    snapshots.Add(
                        new BundleSnapshot
                        {
                            FixedFrame = frame,
                            Checksum = checksum,
                            Label = label,
                            Payload = ReadByteArray("snapshotPayload"),
                        }
                    );
                }

                var sentinel = _buffer.Read<int>("bundleSentinel");
                Assert.IsEqual(sentinel, TrecsConstants.RecordingSentinelValue);

                _buffer.StopRead(verifySentinel: true);

                return new RecordingBundle
                {
                    Header = header,
                    InitialSnapshot = initialSnapshot,
                    InitialSnapshotChecksum = initialSnapshotChecksum,
                    InputQueue = inputQueue,
                    Checksums = checksums,
                    Anchors = anchors,
                    Snapshots = snapshots,
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
                var header = _buffer.Read<BundleHeader>("header");
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
            if (bundle.InitialSnapshot == null)
                throw new ArgumentNullException(nameof(bundle) + ".InitialSnapshot");
            if (bundle.InputQueue == null)
                throw new ArgumentNullException(nameof(bundle) + ".InputQueue");
            if (bundle.Checksums == null)
                throw new ArgumentNullException(nameof(bundle) + ".Checksums");
            if (bundle.Anchors == null)
                throw new ArgumentNullException(nameof(bundle) + ".Anchors");
            if (bundle.Snapshots == null)
                throw new ArgumentNullException(nameof(bundle) + ".Snapshots");
        }

        // Read a length-prefixed byte[] payload, returning an exact-length
        // array. ReadBytes can hand back a buffer larger than the actual
        // payload when reusing pooled storage, so we slice when needed.
        byte[] ReadByteArray(string name)
        {
            byte[] buffer = null;
            var length = _buffer.ReadBytes(name, ref buffer);
            if (buffer == null)
            {
                return Array.Empty<byte>();
            }
            if (buffer.Length == length)
            {
                return buffer;
            }
            var result = new byte[length];
            Buffer.BlockCopy(buffer, 0, result, 0, length);
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
