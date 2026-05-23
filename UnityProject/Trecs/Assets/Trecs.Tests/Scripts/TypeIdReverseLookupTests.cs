using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    // TypeIdReverseLookup is process-wide state — these tests use marker types
    // distinct to each test so cross-test pollution doesn't cause false failures
    // on re-runs in the same AppDomain.
    [TestFixture]
    public class TypeIdReverseLookupTests
    {
        struct IdempotentMarker { }

        struct RemapMarker { }

        struct CollisionMarkerA { }

        struct CollisionMarkerB { }

        struct RoundTripMarker { }

        struct NeverRegisteredMarker { }

        [Test]
        public void Register_SameTypeSameId_IsIdempotent()
        {
            // The early-return path: registering the same (type, id) twice should
            // be a no-op on the second call rather than crashing on duplicate-key.
            var id = new TypeId(0x72000001);
            TypeIdReverseLookup.Register(typeof(IdempotentMarker), id);
            // Second call must succeed silently.
            TypeIdReverseLookup.Register(typeof(IdempotentMarker), id);

            NAssert.IsTrue(TypeIdReverseLookup.IsRegistered(typeof(IdempotentMarker)));
            NAssert.AreEqual(typeof(IdempotentMarker), TypeIdReverseLookup.GetTypeFromId(id));
        }

        [Test]
        public void Register_SameTypeDifferentId_Throws()
        {
            // Remapping a type to a different id is a hard error — it would
            // silently corrupt any code that already cached the old id.
            var firstId = new TypeId(0x72000010);
            var secondId = new TypeId(0x72000011);
            TypeIdReverseLookup.Register(typeof(RemapMarker), firstId);
            var ex = NAssert.Throws<TrecsException>(() =>
                TypeIdReverseLookup.Register(typeof(RemapMarker), secondId)
            );
            StringAssert.Contains("TypeId remapping", ex.Message);
        }

        [Test]
        public void Register_DifferentTypesSameId_Throws()
        {
            // Two distinct types hashing to the same id. Surfaces with the
            // actionable [TypeId] diagnostic, not the internal-desync message.
            var id = new TypeId(0x72000020);
            TypeIdReverseLookup.Register(typeof(CollisionMarkerA), id);
            var ex = NAssert.Throws<TrecsException>(() =>
                TypeIdReverseLookup.Register(typeof(CollisionMarkerB), id)
            );
            StringAssert.Contains("TypeId collision", ex.Message);
        }

        [Test]
        public void GetTypeFromId_RoundTrips()
        {
            var id = new TypeId(0x72000030);
            TypeIdReverseLookup.Register(typeof(RoundTripMarker), id);
            NAssert.AreEqual(typeof(RoundTripMarker), TypeIdReverseLookup.GetTypeFromId(id));
        }

        [Test]
        public void IsRegistered_ReturnsFalseForUnknownType()
        {
            // A type this fixture never explicitly registers — confirms the
            // negative path returns false rather than throwing.
            NAssert.IsFalse(TypeIdReverseLookup.IsRegistered(typeof(NeverRegisteredMarker)));
        }
    }
}
