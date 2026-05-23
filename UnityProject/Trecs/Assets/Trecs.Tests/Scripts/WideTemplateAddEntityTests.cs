using System;
using NUnit.Framework;
using Trecs.Internal;
using Unity.Collections;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // Covers the 256-bit TemplateComponentMask path: a template with > 64
    // components forces SetMask / ZeroDefaultMask to use Word1+, which the
    // existing tests (TwoCompBeta has 2 components) never exercise.
    [TestFixture]
    public partial class WideTemplateAddEntityTests
    {
        struct WideTag : ITag { }

        // 70 distinct component types. Each carries a small distinct int
        // payload so we can verify the Burst drain MemCpy'd the right slot
        // for each component.
        public partial struct W0 : IEntityComponent
        {
            public int V;
        }

        public partial struct W1 : IEntityComponent
        {
            public int V;
        }

        public partial struct W2 : IEntityComponent
        {
            public int V;
        }

        public partial struct W3 : IEntityComponent
        {
            public int V;
        }

        public partial struct W4 : IEntityComponent
        {
            public int V;
        }

        public partial struct W5 : IEntityComponent
        {
            public int V;
        }

        public partial struct W6 : IEntityComponent
        {
            public int V;
        }

        public partial struct W7 : IEntityComponent
        {
            public int V;
        }

        public partial struct W8 : IEntityComponent
        {
            public int V;
        }

        public partial struct W9 : IEntityComponent
        {
            public int V;
        }

        public partial struct W10 : IEntityComponent
        {
            public int V;
        }

        public partial struct W11 : IEntityComponent
        {
            public int V;
        }

        public partial struct W12 : IEntityComponent
        {
            public int V;
        }

        public partial struct W13 : IEntityComponent
        {
            public int V;
        }

        public partial struct W14 : IEntityComponent
        {
            public int V;
        }

        public partial struct W15 : IEntityComponent
        {
            public int V;
        }

        public partial struct W16 : IEntityComponent
        {
            public int V;
        }

        public partial struct W17 : IEntityComponent
        {
            public int V;
        }

        public partial struct W18 : IEntityComponent
        {
            public int V;
        }

        public partial struct W19 : IEntityComponent
        {
            public int V;
        }

        public partial struct W20 : IEntityComponent
        {
            public int V;
        }

        public partial struct W21 : IEntityComponent
        {
            public int V;
        }

        public partial struct W22 : IEntityComponent
        {
            public int V;
        }

        public partial struct W23 : IEntityComponent
        {
            public int V;
        }

        public partial struct W24 : IEntityComponent
        {
            public int V;
        }

        public partial struct W25 : IEntityComponent
        {
            public int V;
        }

        public partial struct W26 : IEntityComponent
        {
            public int V;
        }

        public partial struct W27 : IEntityComponent
        {
            public int V;
        }

        public partial struct W28 : IEntityComponent
        {
            public int V;
        }

        public partial struct W29 : IEntityComponent
        {
            public int V;
        }

        public partial struct W30 : IEntityComponent
        {
            public int V;
        }

        public partial struct W31 : IEntityComponent
        {
            public int V;
        }

        public partial struct W32 : IEntityComponent
        {
            public int V;
        }

        public partial struct W33 : IEntityComponent
        {
            public int V;
        }

        public partial struct W34 : IEntityComponent
        {
            public int V;
        }

        public partial struct W35 : IEntityComponent
        {
            public int V;
        }

        public partial struct W36 : IEntityComponent
        {
            public int V;
        }

        public partial struct W37 : IEntityComponent
        {
            public int V;
        }

        public partial struct W38 : IEntityComponent
        {
            public int V;
        }

        public partial struct W39 : IEntityComponent
        {
            public int V;
        }

        public partial struct W40 : IEntityComponent
        {
            public int V;
        }

        public partial struct W41 : IEntityComponent
        {
            public int V;
        }

        public partial struct W42 : IEntityComponent
        {
            public int V;
        }

        public partial struct W43 : IEntityComponent
        {
            public int V;
        }

        public partial struct W44 : IEntityComponent
        {
            public int V;
        }

        public partial struct W45 : IEntityComponent
        {
            public int V;
        }

        public partial struct W46 : IEntityComponent
        {
            public int V;
        }

        public partial struct W47 : IEntityComponent
        {
            public int V;
        }

        public partial struct W48 : IEntityComponent
        {
            public int V;
        }

        public partial struct W49 : IEntityComponent
        {
            public int V;
        }

        public partial struct W50 : IEntityComponent
        {
            public int V;
        }

        public partial struct W51 : IEntityComponent
        {
            public int V;
        }

        public partial struct W52 : IEntityComponent
        {
            public int V;
        }

        public partial struct W53 : IEntityComponent
        {
            public int V;
        }

        public partial struct W54 : IEntityComponent
        {
            public int V;
        }

        public partial struct W55 : IEntityComponent
        {
            public int V;
        }

        public partial struct W56 : IEntityComponent
        {
            public int V;
        }

        public partial struct W57 : IEntityComponent
        {
            public int V;
        }

        public partial struct W58 : IEntityComponent
        {
            public int V;
        }

        public partial struct W59 : IEntityComponent
        {
            public int V;
        }

        public partial struct W60 : IEntityComponent
        {
            public int V;
        }

        public partial struct W61 : IEntityComponent
        {
            public int V;
        }

        public partial struct W62 : IEntityComponent
        {
            public int V;
        }

        public partial struct W63 : IEntityComponent
        {
            public int V;
        }

        public partial struct W64 : IEntityComponent
        {
            public int V;
        }

        public partial struct W65 : IEntityComponent
        {
            public int V;
        }

        public partial struct W66 : IEntityComponent
        {
            public int V;
        }

        public partial struct W67 : IEntityComponent
        {
            public int V;
        }

        public partial struct W68 : IEntityComponent
        {
            public int V;
        }

        public partial struct W69 : IEntityComponent
        {
            public int V;
        }

        static Template BuildWideTemplate()
        {
            // ComponentDeclaration<T> built via reflection over the 70 W-types
            // so we don't have to enumerate 70 generic instantiations. Same
            // ctor shape the hand-written templates in EcsTestDefinitions use.
            var wideTypes = new Type[]
            {
                typeof(W0),
                typeof(W1),
                typeof(W2),
                typeof(W3),
                typeof(W4),
                typeof(W5),
                typeof(W6),
                typeof(W7),
                typeof(W8),
                typeof(W9),
                typeof(W10),
                typeof(W11),
                typeof(W12),
                typeof(W13),
                typeof(W14),
                typeof(W15),
                typeof(W16),
                typeof(W17),
                typeof(W18),
                typeof(W19),
                typeof(W20),
                typeof(W21),
                typeof(W22),
                typeof(W23),
                typeof(W24),
                typeof(W25),
                typeof(W26),
                typeof(W27),
                typeof(W28),
                typeof(W29),
                typeof(W30),
                typeof(W31),
                typeof(W32),
                typeof(W33),
                typeof(W34),
                typeof(W35),
                typeof(W36),
                typeof(W37),
                typeof(W38),
                typeof(W39),
                typeof(W40),
                typeof(W41),
                typeof(W42),
                typeof(W43),
                typeof(W44),
                typeof(W45),
                typeof(W46),
                typeof(W47),
                typeof(W48),
                typeof(W49),
                typeof(W50),
                typeof(W51),
                typeof(W52),
                typeof(W53),
                typeof(W54),
                typeof(W55),
                typeof(W56),
                typeof(W57),
                typeof(W58),
                typeof(W59),
                typeof(W60),
                typeof(W61),
                typeof(W62),
                typeof(W63),
                typeof(W64),
                typeof(W65),
                typeof(W66),
                typeof(W67),
                typeof(W68),
                typeof(W69),
            };
            var decls = new IComponentDeclaration[wideTypes.Length];
            for (int i = 0; i < wideTypes.Length; i++)
            {
                var t = wideTypes[i];
                var declType = typeof(ComponentDeclaration<>).MakeGenericType(t);
                decls[i] = (IComponentDeclaration)
                    Activator.CreateInstance(
                        declType,
                        new object[] { null, null, null, null, null, Activator.CreateInstance(t) }
                    );
            }
            return new Template(
                debugName: "TestWide70",
                localBaseTemplates: Array.Empty<Template>(),
                partitions: Array.Empty<TagSet>(),
                localComponentDeclarations: decls,
                localTags: new Tag[] { Tag<WideTag>.Value }
            );
        }

        // Sets every one of the 70 W-types with a distinct value (W{N}.V = N),
        // then verifies:
        //   - SetMask shows 70 bits set: Word0 full (64 bits), Word1 low 6
        //     bits, Word2+Word3 zero — exercises the Word0 → Word1 crossover.
        //   - After Submit, every component round-trips through the Burst
        //     drain with the right value. The accessor reads are typed, so
        //     which slot index a given W-type occupies in the template's
        //     layout doesn't matter — every type is queried by its own
        //     Component<T> overload.
        [Test]
        public unsafe void AddEntity_TemplateWithOver64Components_SetMaskTracksHighWords()
        {
            using var env = EcsTestHelper.CreateEnvironment(BuildWideTemplate());
            var a = env.Accessor;
            var wideTag = Tag<WideTag>.Value;
            var group = a.WorldInfo.GetSingleGroupWithTags(wideTag);
            var bags = env.Accessor.World.EntitySubmitter.PerGroupAddBags;

            NAssert.AreEqual(70, a.WorldInfo.ComponentLayouts.Headers[group.Index].ComponentCount);

            var refs = a.ReserveEntityHandles(1, Allocator.Temp);
            var nativeEcs = a.ToNative();
            var init = nativeEcs.AddEntity(wideTag, sortKey: 0, refs[0]);

            init = init.Set(new W0 { V = 0 });
            init = init.Set(new W1 { V = 1 });
            init = init.Set(new W2 { V = 2 });
            init = init.Set(new W3 { V = 3 });
            init = init.Set(new W4 { V = 4 });
            init = init.Set(new W5 { V = 5 });
            init = init.Set(new W6 { V = 6 });
            init = init.Set(new W7 { V = 7 });
            init = init.Set(new W8 { V = 8 });
            init = init.Set(new W9 { V = 9 });
            init = init.Set(new W10 { V = 10 });
            init = init.Set(new W11 { V = 11 });
            init = init.Set(new W12 { V = 12 });
            init = init.Set(new W13 { V = 13 });
            init = init.Set(new W14 { V = 14 });
            init = init.Set(new W15 { V = 15 });
            init = init.Set(new W16 { V = 16 });
            init = init.Set(new W17 { V = 17 });
            init = init.Set(new W18 { V = 18 });
            init = init.Set(new W19 { V = 19 });
            init = init.Set(new W20 { V = 20 });
            init = init.Set(new W21 { V = 21 });
            init = init.Set(new W22 { V = 22 });
            init = init.Set(new W23 { V = 23 });
            init = init.Set(new W24 { V = 24 });
            init = init.Set(new W25 { V = 25 });
            init = init.Set(new W26 { V = 26 });
            init = init.Set(new W27 { V = 27 });
            init = init.Set(new W28 { V = 28 });
            init = init.Set(new W29 { V = 29 });
            init = init.Set(new W30 { V = 30 });
            init = init.Set(new W31 { V = 31 });
            init = init.Set(new W32 { V = 32 });
            init = init.Set(new W33 { V = 33 });
            init = init.Set(new W34 { V = 34 });
            init = init.Set(new W35 { V = 35 });
            init = init.Set(new W36 { V = 36 });
            init = init.Set(new W37 { V = 37 });
            init = init.Set(new W38 { V = 38 });
            init = init.Set(new W39 { V = 39 });
            init = init.Set(new W40 { V = 40 });
            init = init.Set(new W41 { V = 41 });
            init = init.Set(new W42 { V = 42 });
            init = init.Set(new W43 { V = 43 });
            init = init.Set(new W44 { V = 44 });
            init = init.Set(new W45 { V = 45 });
            init = init.Set(new W46 { V = 46 });
            init = init.Set(new W47 { V = 47 });
            init = init.Set(new W48 { V = 48 });
            init = init.Set(new W49 { V = 49 });
            init = init.Set(new W50 { V = 50 });
            init = init.Set(new W51 { V = 51 });
            init = init.Set(new W52 { V = 52 });
            init = init.Set(new W53 { V = 53 });
            init = init.Set(new W54 { V = 54 });
            init = init.Set(new W55 { V = 55 });
            init = init.Set(new W56 { V = 56 });
            init = init.Set(new W57 { V = 57 });
            init = init.Set(new W58 { V = 58 });
            init = init.Set(new W59 { V = 59 });
            init = init.Set(new W60 { V = 60 });
            init = init.Set(new W61 { V = 61 });
            init = init.Set(new W62 { V = 62 });
            init = init.Set(new W63 { V = 63 });
            init = init.Set(new W64 { V = 64 });
            init = init.Set(new W65 { V = 65 });
            init = init.Set(new W66 { V = 66 });
            init = init.Set(new W67 { V = 67 });
            init = init.Set(new W68 { V = 68 });
            init = init.Set(new W69 { V = 69 });

            // 70 bits set across Word0 (all 64) + Word1 (low 6); Word2/Word3 zero.
            // Exercises the Word0 → Word1 crossover that the original
            // ulong-only encoding could not represent.
            var cell = bags.GetCell(0, group.Index);
            var slotHdr = (FastAddSlotHeader*)cell.Ptr;
            NAssert.AreEqual(0xFFFFFFFFFFFFFFFFul, slotHdr->SetMask.Word0);
            NAssert.AreEqual(0b111111ul, slotHdr->SetMask.Word1);
            NAssert.AreEqual(0ul, slotHdr->SetMask.Word2);
            NAssert.AreEqual(0ul, slotHdr->SetMask.Word3);

            refs.Dispose();
            a.Submit();

            // After drain: every component round-trips with its set value.
            NAssert.AreEqual(1, a.CountEntitiesWithTags(wideTag));
            var e = new EntityIndex(0, group);
            NAssert.AreEqual(0, a.Component<W0>(e).Read.V);
            NAssert.AreEqual(1, a.Component<W1>(e).Read.V);
            NAssert.AreEqual(2, a.Component<W2>(e).Read.V);
            NAssert.AreEqual(3, a.Component<W3>(e).Read.V);
            NAssert.AreEqual(4, a.Component<W4>(e).Read.V);
            NAssert.AreEqual(5, a.Component<W5>(e).Read.V);
            NAssert.AreEqual(6, a.Component<W6>(e).Read.V);
            NAssert.AreEqual(7, a.Component<W7>(e).Read.V);
            NAssert.AreEqual(8, a.Component<W8>(e).Read.V);
            NAssert.AreEqual(9, a.Component<W9>(e).Read.V);
            NAssert.AreEqual(10, a.Component<W10>(e).Read.V);
            NAssert.AreEqual(11, a.Component<W11>(e).Read.V);
            NAssert.AreEqual(12, a.Component<W12>(e).Read.V);
            NAssert.AreEqual(13, a.Component<W13>(e).Read.V);
            NAssert.AreEqual(14, a.Component<W14>(e).Read.V);
            NAssert.AreEqual(15, a.Component<W15>(e).Read.V);
            NAssert.AreEqual(16, a.Component<W16>(e).Read.V);
            NAssert.AreEqual(17, a.Component<W17>(e).Read.V);
            NAssert.AreEqual(18, a.Component<W18>(e).Read.V);
            NAssert.AreEqual(19, a.Component<W19>(e).Read.V);
            NAssert.AreEqual(20, a.Component<W20>(e).Read.V);
            NAssert.AreEqual(21, a.Component<W21>(e).Read.V);
            NAssert.AreEqual(22, a.Component<W22>(e).Read.V);
            NAssert.AreEqual(23, a.Component<W23>(e).Read.V);
            NAssert.AreEqual(24, a.Component<W24>(e).Read.V);
            NAssert.AreEqual(25, a.Component<W25>(e).Read.V);
            NAssert.AreEqual(26, a.Component<W26>(e).Read.V);
            NAssert.AreEqual(27, a.Component<W27>(e).Read.V);
            NAssert.AreEqual(28, a.Component<W28>(e).Read.V);
            NAssert.AreEqual(29, a.Component<W29>(e).Read.V);
            NAssert.AreEqual(30, a.Component<W30>(e).Read.V);
            NAssert.AreEqual(31, a.Component<W31>(e).Read.V);
            NAssert.AreEqual(32, a.Component<W32>(e).Read.V);
            NAssert.AreEqual(33, a.Component<W33>(e).Read.V);
            NAssert.AreEqual(34, a.Component<W34>(e).Read.V);
            NAssert.AreEqual(35, a.Component<W35>(e).Read.V);
            NAssert.AreEqual(36, a.Component<W36>(e).Read.V);
            NAssert.AreEqual(37, a.Component<W37>(e).Read.V);
            NAssert.AreEqual(38, a.Component<W38>(e).Read.V);
            NAssert.AreEqual(39, a.Component<W39>(e).Read.V);
            NAssert.AreEqual(40, a.Component<W40>(e).Read.V);
            NAssert.AreEqual(41, a.Component<W41>(e).Read.V);
            NAssert.AreEqual(42, a.Component<W42>(e).Read.V);
            NAssert.AreEqual(43, a.Component<W43>(e).Read.V);
            NAssert.AreEqual(44, a.Component<W44>(e).Read.V);
            NAssert.AreEqual(45, a.Component<W45>(e).Read.V);
            NAssert.AreEqual(46, a.Component<W46>(e).Read.V);
            NAssert.AreEqual(47, a.Component<W47>(e).Read.V);
            NAssert.AreEqual(48, a.Component<W48>(e).Read.V);
            NAssert.AreEqual(49, a.Component<W49>(e).Read.V);
            NAssert.AreEqual(50, a.Component<W50>(e).Read.V);
            NAssert.AreEqual(51, a.Component<W51>(e).Read.V);
            NAssert.AreEqual(52, a.Component<W52>(e).Read.V);
            NAssert.AreEqual(53, a.Component<W53>(e).Read.V);
            NAssert.AreEqual(54, a.Component<W54>(e).Read.V);
            NAssert.AreEqual(55, a.Component<W55>(e).Read.V);
            NAssert.AreEqual(56, a.Component<W56>(e).Read.V);
            NAssert.AreEqual(57, a.Component<W57>(e).Read.V);
            NAssert.AreEqual(58, a.Component<W58>(e).Read.V);
            NAssert.AreEqual(59, a.Component<W59>(e).Read.V);
            NAssert.AreEqual(60, a.Component<W60>(e).Read.V);
            NAssert.AreEqual(61, a.Component<W61>(e).Read.V);
            NAssert.AreEqual(62, a.Component<W62>(e).Read.V);
            NAssert.AreEqual(63, a.Component<W63>(e).Read.V);
            NAssert.AreEqual(64, a.Component<W64>(e).Read.V);
            NAssert.AreEqual(65, a.Component<W65>(e).Read.V);
            NAssert.AreEqual(66, a.Component<W66>(e).Read.V);
            NAssert.AreEqual(67, a.Component<W67>(e).Read.V);
            NAssert.AreEqual(68, a.Component<W68>(e).Read.V);
            NAssert.AreEqual(69, a.Component<W69>(e).Read.V);
        }
    }
}
