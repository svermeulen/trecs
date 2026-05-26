using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class TrecsDictionaryTests
    {
        static NativeHeap CreateChunkStore() => new NativeHeap(TrecsLog.Default);

        [Test]
        public void Default_IsNull()
        {
            NAssert.IsTrue(default(TrecsDictionary<int, float>).IsNull);
        }

        [Test]
        public void Alloc_ReturnsNonNullHandle_AndZeroCount()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs);

            NAssert.IsFalse(dict.IsNull);
            var read = dict.Read(cs.Resolver);
            NAssert.AreEqual(0, read.Count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void AllocWithInitialCapacity_PresizesBuffer()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 16);

            var read = dict.Read(cs.Resolver);
            NAssert.AreEqual(0, read.Count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Add_InsertsValue_AndUpdatesCount()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 1.5f);
            w.Add(20, 2.5f);
            w.Add(30, 3.5f);

            NAssert.AreEqual(3, w.Count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void TryGetValue_ReturnsCorrectValues()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 1.5f);
            w.Add(20, 2.5f);

            var read = dict.Read(cs.Resolver);
            NAssert.IsTrue(read.TryGetValue(10, out var v1));
            NAssert.AreEqual(1.5f, v1);
            NAssert.IsTrue(read.TryGetValue(20, out var v2));
            NAssert.AreEqual(2.5f, v2);
            NAssert.IsFalse(read.TryGetValue(99, out _));

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void ContainsKey_ReturnsTrueForExistingKeys()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(42, 1.0f);

            var read = dict.Read(cs.Resolver);
            NAssert.IsTrue(read.ContainsKey(42));
            NAssert.IsFalse(read.ContainsKey(99));

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Indexer_Get_ReturnsValue()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(5, 55.0f);

            var read = dict.Read(cs.Resolver);
            NAssert.AreEqual(55.0f, read[5]);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Indexer_Set_AddsOrUpdates()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w[10] = 1.0f;
            w[10] = 2.0f;
            w[20] = 3.0f;

            NAssert.AreEqual(2, w.Count);

            var read = dict.Read(cs.Resolver);
            NAssert.AreEqual(2.0f, read[10]);
            NAssert.AreEqual(3.0f, read[20]);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Set_UpdatesExistingKey()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(5, 1.0f);
            w.Set(5, 99.0f);

            var read = dict.Read(cs.Resolver);
            NAssert.AreEqual(99.0f, read[5]);
            NAssert.AreEqual(1, read.Count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void TryAdd_ReturnsFalseOnDuplicate()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            NAssert.IsTrue(w.TryAdd(10, 1.0f));
            NAssert.IsFalse(w.TryAdd(10, 2.0f));
            NAssert.AreEqual(1, w.Count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Remove_RemovesKey_AndReturnsTrue()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 1.0f);
            w.Add(20, 2.0f);
            w.Add(30, 3.0f);

            NAssert.IsTrue(w.Remove(20, out var removed));
            NAssert.AreEqual(2.0f, removed);
            NAssert.AreEqual(2, w.Count);

            var read = dict.Read(cs.Resolver);
            NAssert.IsFalse(read.ContainsKey(20));
            NAssert.IsTrue(read.ContainsKey(10));
            NAssert.IsTrue(read.ContainsKey(30));

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Remove_ReturnsFalseForMissingKey()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 1.0f);

            NAssert.IsFalse(w.Remove(99));
            NAssert.AreEqual(1, w.Count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Clear_ResetsCountToZero()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 1.0f);
            w.Add(20, 2.0f);
            w.Clear();

            NAssert.AreEqual(0, w.Count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Clear_ThenAddAgain_Works()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 1.0f);
            w.Clear();
            w.Add(20, 2.0f);

            NAssert.AreEqual(1, w.Count);
            var read = dict.Read(cs.Resolver);
            NAssert.IsTrue(read.ContainsKey(20));
            NAssert.IsFalse(read.ContainsKey(10));

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void GetOrAdd_ReturnsExistingValue()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 5.0f);
            ref var val = ref w.GetOrAdd(10);
            NAssert.AreEqual(5.0f, val);
            NAssert.AreEqual(1, w.Count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void GetOrAdd_InsertsDefault_WhenKeyMissing()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            ref var val = ref w.GetOrAdd(10);
            NAssert.AreEqual(0.0f, val);
            NAssert.AreEqual(1, w.Count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void GetValueByRef_ReturnsWritableRef()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 1.0f);
            ref var val = ref w.GetValueByRef(10);
            val = 42.0f;

            var read = dict.Read(cs.Resolver);
            NAssert.AreEqual(42.0f, read[10]);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void AutoGrow_FromZeroCapacity()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs);
            var w = dict.Write(cs);

            w.Add(1, 1.0f);
            w.Add(2, 2.0f);
            w.Add(3, 3.0f);

            NAssert.AreEqual(3, w.Count);
            var read = dict.Read(cs.Resolver);
            NAssert.AreEqual(1.0f, read[1]);
            NAssert.AreEqual(2.0f, read[2]);
            NAssert.AreEqual(3.0f, read[3]);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void AutoGrow_PastInitialCapacity()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, int>(cs, 4);
            var w = dict.Write(cs);

            for (int i = 0; i < 100; i++)
                w.Add(i, i * 10);

            NAssert.AreEqual(100, w.Count);

            var read = dict.Read(cs.Resolver);
            for (int i = 0; i < 100; i++)
                NAssert.AreEqual(i * 10, read[i]);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void EnsureCapacity_ThenWriteFromNative_Works()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            dict.EnsureCapacity(cs, 50);

            var nw = dict.Write(cs.Resolver);
            for (int i = 0; i < 50; i++)
                nw.Add(i, (float)i);

            NAssert.AreEqual(50, nw.Count);

            var read = dict.Read(cs.Resolver);
            for (int i = 0; i < 50; i++)
                NAssert.AreEqual((float)i, read[i]);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Foreach_Read_IteratesInInsertionOrder()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 8);
            var w = dict.Write(cs);

            w.Add(100, 1.0f);
            w.Add(200, 2.0f);
            w.Add(300, 3.0f);

            var read = dict.Read(cs.Resolver);
            int idx = 0;
            int[] expectedKeys = { 100, 200, 300 };
            float[] expectedValues = { 1.0f, 2.0f, 3.0f };

            foreach (var (key, value) in read)
            {
                NAssert.AreEqual(expectedKeys[idx], key);
                NAssert.AreEqual(expectedValues[idx], value);
                idx++;
            }
            NAssert.AreEqual(3, idx);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Foreach_Write_IteratesInInsertionOrder()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 8);
            var w = dict.Write(cs);

            w.Add(100, 1.0f);
            w.Add(200, 2.0f);
            w.Add(300, 3.0f);

            int idx = 0;
            int[] expectedKeys = { 100, 200, 300 };
            float[] expectedValues = { 1.0f, 2.0f, 3.0f };

            foreach (var (key, value) in w)
            {
                NAssert.AreEqual(expectedKeys[idx], key);
                NAssert.AreEqual(expectedValues[idx], value);
                idx++;
            }
            NAssert.AreEqual(3, idx);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Keys_IteratesAllKeys()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(5, 1.0f);
            w.Add(10, 2.0f);
            w.Add(15, 3.0f);

            var read = dict.Read(cs);
            int count = 0;
            foreach (var key in read.Keys)
            {
                NAssert.IsTrue(key == 5 || key == 10 || key == 15);
                count++;
            }
            NAssert.AreEqual(3, count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void GetValueAtIndex_ReadsCorrectly()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 1.0f);
            w.Add(20, 2.0f);

            var read = dict.Read(cs.Resolver);
            ref readonly var v0 = ref read.GetValueAtIndex(0);
            ref readonly var v1 = ref read.GetValueAtIndex(1);
            NAssert.AreEqual(1.0f, v0);
            NAssert.AreEqual(2.0f, v1);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void GetKeyAtIndex_ReadsCorrectly()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(10, 1.0f);
            w.Add(20, 2.0f);

            var read = dict.Read(cs.Resolver);
            NAssert.AreEqual(10, read.GetKeyAtIndex(0));
            NAssert.AreEqual(20, read.GetKeyAtIndex(1));

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Remove_SwapBack_MaintainsHashIntegrity()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 8);
            var w = dict.Write(cs);

            for (int i = 0; i < 8; i++)
                w.Add(i, (float)(i * 10));

            // Remove from middle
            w.Remove(3);
            w.Remove(5);

            NAssert.AreEqual(6, w.Count);

            // Verify all remaining keys are still findable
            var read = dict.Read(cs.Resolver);
            NAssert.IsTrue(read.ContainsKey(0));
            NAssert.IsTrue(read.ContainsKey(1));
            NAssert.IsTrue(read.ContainsKey(2));
            NAssert.IsFalse(read.ContainsKey(3));
            NAssert.IsTrue(read.ContainsKey(4));
            NAssert.IsFalse(read.ContainsKey(5));
            NAssert.IsTrue(read.ContainsKey(6));
            NAssert.IsTrue(read.ContainsKey(7));

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Remove_AllEntries_ThenAddAgain()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);

            w.Add(1, 1.0f);
            w.Add(2, 2.0f);
            w.Add(3, 3.0f);

            w.Remove(1);
            w.Remove(2);
            w.Remove(3);

            NAssert.AreEqual(0, w.Count);

            w.Add(10, 10.0f);
            NAssert.AreEqual(1, w.Count);

            var read = dict.Read(cs.Resolver);
            NAssert.AreEqual(10.0f, read[10]);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void Dispose_FreesAllocations()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);
            w.Add(1, 1.0f);

            NAssert.AreEqual(2, cs.NumLiveAllocations); // header + data
            dict.Dispose(cs);
            NAssert.AreEqual(0, cs.NumLiveAllocations);

            cs.Dispose();
        }

        [Test]
        public void Handle_Equality_Works()
        {
            var cs = CreateChunkStore();
            var dict1 = TrecsDictionary.Alloc<int, float>(cs, 4);
            var dict2 = TrecsDictionary.Alloc<int, float>(cs, 4);

#pragma warning disable CS1718
            NAssert.IsTrue(dict1 == dict1);
#pragma warning restore CS1718
            NAssert.IsFalse(dict1 == dict2);
            NAssert.IsTrue(dict1 != dict2);
            NAssert.AreEqual(dict1.GetHashCode(), dict1.GetHashCode());

            dict1.Dispose(cs);
            dict2.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void ManyCollisions_TriggersRehash_AndMaintainsIntegrity()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, int>(cs);
            var w = dict.Write(cs);

            for (int i = 0; i < 200; i++)
                w.Add(i, i);

            NAssert.AreEqual(200, w.Count);

            var read = dict.Read(cs.Resolver);
            for (int i = 0; i < 200; i++)
                NAssert.AreEqual(i, read[i]);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void VersionStaleness_ManagedWriteWrapper_DetectsExternalMutation()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w1 = dict.Write(cs);
            w1.Add(1, 1.0f);

            var w2 = dict.Write(cs);
            w2.Add(2, 2.0f);

            // Managed wrappers check version per-access — catches staleness.
            bool threw = false;
            try
            {
                var _ = w1.Count;
            }
            catch (TrecsException)
            {
                threw = true;
            }
            NAssert.IsTrue(threw, "Managed write wrapper should detect stale version");

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void VersionStaleness_ManagedReadWrapper_DetectsMutation()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);
            w.Add(1, 1.0f);

            var read = dict.Read(cs);
            NAssert.AreEqual(1, read.Count);

            var w2 = dict.Write(cs);
            w2.Add(2, 2.0f);

            // Managed wrappers check version per-access — catches staleness.
            bool threw = false;
            try
            {
                var _ = read.Count;
            }
            catch (TrecsException)
            {
                threw = true;
            }
            NAssert.IsTrue(threw, "Managed read wrapper should detect stale version");

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void VersionStaleness_NativeWrappers_NoPerAccessCheck()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w1 = dict.Write(cs.Resolver);
            w1.Add(1, 1.0f);

            var w2 = dict.Write(cs.Resolver);
            w2.Add(2, 2.0f);

            // Native wrappers validate at construction only, matching NativeList semantics.
            NAssert.DoesNotThrow(() =>
            {
                var _ = w1.Count;
            });

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void NativeWrite_CapacityExceeded_Throws()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 2);
            var nw = dict.Write(cs.Resolver);

            nw.Add(1, 1.0f);
            nw.Add(2, 2.0f);

            NAssert.Throws<TrecsException>(() => nw.Add(3, 3.0f));

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void NativeRead_Foreach_Works()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            var w = dict.Write(cs);
            w.Add(10, 1.0f);
            w.Add(20, 2.0f);

            var nr = dict.Read(cs.Resolver);
            int count = 0;
            foreach (var (key, value) in nr)
            {
                NAssert.IsTrue(key == 10 || key == 20);
                count++;
            }
            NAssert.AreEqual(2, count);

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void NativeWrite_Remove_Works()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 8);
            var nw = dict.Write(cs.Resolver);

            nw.Add(1, 10.0f);
            nw.Add(2, 20.0f);
            nw.Add(3, 30.0f);

            NAssert.IsTrue(nw.Remove(2, out var removed));
            NAssert.AreEqual(20.0f, removed);
            NAssert.AreEqual(2, nw.Count);
            NAssert.IsFalse(nw.ContainsKey(2));

            dict.Dispose(cs);
            cs.Dispose();
        }

        [Test]
        public void NativeWrite_Foreach_Works()
        {
            var cs = CreateChunkStore();
            var dict = TrecsDictionary.Alloc<int, float>(cs, 4);
            dict.EnsureCapacity(cs, 4);
            var nw = dict.Write(cs.Resolver);
            nw.Add(10, 1.0f);
            nw.Add(20, 2.0f);

            int count = 0;
            foreach (var (key, value) in nw)
            {
                NAssert.IsTrue(key == 10 || key == 20);
                count++;
            }
            NAssert.AreEqual(2, count);

            dict.Dispose(cs);
            cs.Dispose();
        }
    }
}
