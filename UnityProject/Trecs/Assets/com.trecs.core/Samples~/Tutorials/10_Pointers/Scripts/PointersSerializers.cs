using System.Collections.Generic;
using UnityEngine;

namespace Trecs.Samples.Pointers
{
    // TrailHistory lives on the heap behind UniquePtr<T> because it holds
    // List<Vector3>, a managed collection that can't sit in an unmanaged
    // ECS component. The standard Trecs blit serializer can only handle
    // unmanaged structs, so the managed payload type needs its own
    // ISerializer<T> registered against the SerializerRegistry.
    //
    // Registration happens in PointersCompositionRoot.Construct against
    // world.SerializerRegistry. Once registered,
    // the Trecs Player window can take and restore snapshots of this world:
    // when the snapshot writer encounters a UniquePtr<TrailHistory>, it
    // dereferences the pointer and hands the TrailHistory instance off to
    // the serializer below; on load, Deserialize rebuilds the instance and
    // re-allocates the heap slot behind the UniquePtr.

    public sealed class TrailHistorySerializer : ISerializer<TrailHistory>
    {
        public void Serialize(in TrailHistory value, ISerializationWriter writer)
        {
            writer.Write("positionCount", value.Positions.Count);
            foreach (var position in value.Positions)
                writer.Write("position", position);

            writer.Write("maxLength", value.MaxLength);
        }

        public void Deserialize(ref TrailHistory value, ISerializationReader reader)
        {
            value ??= new TrailHistory();

            var count = reader.Read<int>("positionCount");
            value.Positions ??= new List<Vector3>(count);
            value.Positions.Clear();
            for (int i = 0; i < count; i++)
                value.Positions.Add(reader.Read<Vector3>("position"));

            value.MaxLength = reader.Read<int>("maxLength");
        }
    }
}
