using Trecs.Collections;

namespace Trecs.Serialization.Internal
{
    public sealed class RngSerializer : ISerializer<Rng>
    {
        public void Serialize(in Rng value, ISerializationWriter writer)
        {
            var (s0, s1, s2, s3) = value.GetState();
            writer.Write<uint>("s0", s0);
            writer.Write<uint>("s1", s1);
            writer.Write<uint>("s2", s2);
            writer.Write<uint>("s3", s3);
        }

        public void Deserialize(ref Rng value, ISerializationReader reader)
        {
            var s0 = reader.Read<uint>("s0");
            var s1 = reader.Read<uint>("s1");
            var s2 = reader.Read<uint>("s2");
            var s3 = reader.Read<uint>("s3");
            value.SetState(s0, s1, s2, s3);
        }
    }
}
