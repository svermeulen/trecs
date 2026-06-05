using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    /// <summary>
    /// Unit tests for <see cref="FileBlobStore"/> — id ↔ bytes on disk, with recording-rooted GC.
    /// </summary>
    [TestFixture]
    public class FileBlobStoreTests
    {
        string _tempDir;
        SerializerRegistry _registry;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(
                Path.GetTempPath(),
                "trecs_fileblob_" + Path.GetRandomFileName()
            );
            _registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(_registry);
        }

        [TearDown]
        public void TearDown()
        {
            if (_tempDir != null && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }

        // The store is stream-based; these bridge the byte[] payloads the round-trip tests assert on.
        static void WriteBytes(FileBlobStore store, BlobId id, byte[] payload) =>
            store.Write(id, s => s.Write(payload, 0, payload.Length));

        static bool TryReadBytes(FileBlobStore store, BlobId id, out byte[] bytes)
        {
            if (!store.TryOpenRead(id, out var stream))
            {
                bytes = null;
                return false;
            }
            using (stream)
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                bytes = ms.ToArray();
                return true;
            }
        }

        [Test]
        public void WriteReadContainsDelete_RoundTrips()
        {
            var store = new FileBlobStore(_tempDir);
            var id = new BlobId(7);

            NAssert.IsFalse(store.Contains(id));
            NAssert.IsFalse(TryReadBytes(store, id, out _));

            var payload = new byte[] { 1, 2, 3, 4, 5 };
            WriteBytes(store, id, payload);

            NAssert.IsTrue(store.Contains(id));
            NAssert.IsTrue(TryReadBytes(store, id, out var readBack));
            NAssert.AreEqual(payload, readBack);

            NAssert.IsTrue(store.Delete(id));
            NAssert.IsFalse(store.Contains(id));
            NAssert.IsFalse(store.Delete(id));
        }

        [Test]
        public void Write_OverwritesExisting()
        {
            var store = new FileBlobStore(_tempDir);
            var id = new BlobId(7);

            WriteBytes(store, id, new byte[] { 1, 2, 3 });
            WriteBytes(store, id, new byte[] { 9, 9 });

            NAssert.IsTrue(TryReadBytes(store, id, out var bytes));
            NAssert.AreEqual(new byte[] { 9, 9 }, bytes);
        }

        [Test]
        public void EnumerateIds_ReturnsAllStored()
        {
            var store = new FileBlobStore(_tempDir);
            WriteBytes(store, new BlobId(1), new byte[] { 1 });
            WriteBytes(store, new BlobId(-2), new byte[] { 2 });
            WriteBytes(store, new BlobId(3), new byte[] { 3 });

            var ids = store.EnumerateIds().ToHashSet();

            NAssert.AreEqual(3, ids.Count);
            NAssert.IsTrue(ids.Contains(new BlobId(1)));
            NAssert.IsTrue(ids.Contains(new BlobId(-2)));
            NAssert.IsTrue(ids.Contains(new BlobId(3)));
        }

        [Test]
        public void CollectGarbage_DeletesUnreferenced_KeepsLive()
        {
            var store = new FileBlobStore(_tempDir);
            for (int i = 1; i <= 5; i++)
            {
                WriteBytes(store, new BlobId(i), new byte[] { (byte)i });
            }

            var live = new HashSet<BlobId> { new BlobId(2), new BlobId(4) };
            int deleted = store.CollectGarbage(live);

            NAssert.AreEqual(3, deleted);
            NAssert.IsFalse(store.Contains(new BlobId(1)));
            NAssert.IsTrue(store.Contains(new BlobId(2)));
            NAssert.IsFalse(store.Contains(new BlobId(3)));
            NAssert.IsTrue(store.Contains(new BlobId(4)));
            NAssert.IsFalse(store.Contains(new BlobId(5)));
        }
    }
}
