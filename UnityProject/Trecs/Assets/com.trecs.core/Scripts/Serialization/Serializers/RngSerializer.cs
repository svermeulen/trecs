using Trecs.Collections;

namespace Trecs.Serialization
{
    public sealed class RngSerializer : ISerializer<Rng>
    {
        public void Serialize(in Rng value, ISerializationWriter writer)
        {
            var (s0, s1, s2, s3) = value.GetState();
            writer.Write<uint>("S0", s0);
            writer.Write<uint>("S1", s1);
            writer.Write<uint>("S2", s2);
            writer.Write<uint>("S3", s3);
        }

        public void Deserialize(ref Rng value, ISerializationReader reader)
        {
            var s0 = reader.Read<uint>("S0");
            var s1 = reader.Read<uint>("S1");
            var s2 = reader.Read<uint>("S2");
            var s3 = reader.Read<uint>("S3");
            value.SetState(s0, s1, s2, s3);
        }
    }
}
