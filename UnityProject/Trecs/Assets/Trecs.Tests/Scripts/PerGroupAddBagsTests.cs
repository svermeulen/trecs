using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class PerGroupAddBagsTests
    {
        [Test]
        public void Create_GivesAllCellsEmpty()
        {
            var bags = PerGroupAddBags.Create(new int[] { 4, 4, 4 }, Allocator.Persistent);

            for (int t = 0; t < bags.ThreadSlotCount; t++)
            {
                for (int g = 0; g < bags.GroupCount; g++)
                {
                    NAssert.AreEqual(0, bags.GetCell(t, g).Length);
                }
            }

            bags.Dispose();
        }

        [Test]
        public void SlotSize_ReturnsPerGroupValue()
        {
            var bags = PerGroupAddBags.Create(new int[] { 16, 32, 64 }, Allocator.Persistent);

            NAssert.AreEqual(16, bags.SlotSize(0));
            NAssert.AreEqual(32, bags.SlotSize(1));
            NAssert.AreEqual(64, bags.SlotSize(2));

            bags.Dispose();
        }

        [Test]
        public unsafe void AppendSlot_AdvancesCellLengthByPerGroupSlotSize()
        {
            var bags = PerGroupAddBags.Create(new int[] { 8, 16 }, Allocator.Persistent);

            byte* p1 = bags.AppendSlot(0, 0);
            byte* p2 = bags.AppendSlot(0, 0);
            byte* p3 = bags.AppendSlot(0, 1);

            NAssert.AreEqual(16, bags.GetCell(0, 0).Length);
            NAssert.AreEqual(16, bags.GetCell(0, 1).Length);
            NAssert.IsTrue(p2 == p1 + 8);
            NAssert.IsTrue(p3 != null);

            bags.Dispose();
        }

        [Test]
        public unsafe void AppendSlot_IsolatedPerCell()
        {
            var bags = PerGroupAddBags.Create(new int[] { 4, 4 }, Allocator.Persistent);

            byte* a = bags.AppendSlot(0, 0);
            byte* b = bags.AppendSlot(1, 0);
            *(int*)a = 0x11111111;
            *(int*)b = 0x22222222;

            NAssert.AreEqual(0x11111111, *(int*)bags.GetCell(0, 0).Ptr);
            NAssert.AreEqual(0x22222222, *(int*)bags.GetCell(1, 0).Ptr);

            bags.Dispose();
        }

        [Test]
        public unsafe void Clear_EmptiesAllCells()
        {
            var bags = PerGroupAddBags.Create(new int[] { 4 }, Allocator.Persistent);

            bags.AppendSlot(0, 0);
            bags.AppendSlot(1, 0);
            NAssert.AreEqual(4, bags.GetCell(0, 0).Length);
            NAssert.AreEqual(4, bags.GetCell(1, 0).Length);

            bags.Clear();

            for (int t = 0; t < bags.ThreadSlotCount; t++)
            {
                NAssert.AreEqual(0, bags.GetCell(t, 0).Length);
            }

            bags.Dispose();
        }
    }
}
