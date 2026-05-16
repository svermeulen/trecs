using System.Collections.Generic;
using Trecs.Internal;
using Trecs.Serialization;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Tests
{
    public static class TestSerializerInstaller
    {
        public static void RegisterTestCollectionSerializers(SerializerRegistry registry)
        {
            // Collection serializers for testing
            registry.RegisterSerializer(new ListSerializer<int>());
            registry.RegisterSerializer(new ListSerializer<string>());
            registry.RegisterSerializer(new DictionarySerializer<string, int>());
            registry.RegisterSerializer(new DictionarySerializer<int, string>());
            registry.RegisterSerializer(new DictionarySerializer<string, string>());
            registry.RegisterSerializer(new DictionarySerializer<int, int>());
            registry.RegisterSerializer(new DictionarySerializer<string, List<int>>());
            registry.RegisterSerializer(new HashSetSerializer<int>());
            registry.RegisterSerializer(new HashSetSerializer<string>());

            // Array serializers for malformed collection tests
            registry.RegisterSerializer(new BlitArraySerializer<int>());
            registry.RegisterSerializer(new ArraySerializer<string>());

            // Nested collection serializers for malformed tests
            registry.RegisterSerializer(new ListSerializer<List<int>>());

            // Test class serializer for null serialization tests (the JSON
            // counterpart lives in JsonNullSerializationTests itself so the
            // public exporter can drop it together with the JSON tests).
            registry.RegisterSerializer(new TestClassSerializer());

            // FastList blit serializers for performance testing
            registry.RegisterSerializer(new FastListSerializer<int>());
            registry.RegisterSerializer(new FastListSerializer<float>());
            registry.RegisterSerializer(new FastListSerializer<Vector3>());
            registry.RegisterSerializer(new FastListSerializer<int2>());
            registry.RegisterSerializer(new FastListSerializer<byte>());

            // DenseDictionary serializers for blit performance testing
            registry.RegisterSerializer(new DenseDictionarySerializer<int, float>());
            registry.RegisterSerializer(new DenseDictionarySerializer<int, int>());
            registry.RegisterSerializer(new DenseDictionarySerializer<int, Vector3>());
            registry.RegisterSerializer(new DenseDictionarySerializer<int2, float3>());
            registry.RegisterSerializer(new DenseDictionarySerializer<int, string>());
        }

        public static SerializerRegistry CreateTestRegistry()
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            RegisterTestCollectionSerializers(registry);
            return registry;
        }

        public static SerializerRegistry CreateJsonTestRegistry()
        {
            var registry = new SerializerRegistry();
            DefaultTrecsSerializers.RegisterCommonTrecsSerializers(registry);
            return registry;
        }

        public class TestClassSerializer : ISerializer<NullSerializationTests.TestClass>
        {
            public TestClassSerializer() { }

            public void Serialize(
                in NullSerializationTests.TestClass value,
                ISerializationWriter writer
            )
            {
                writer.Write("Value", value.Value);
                writer.WriteString("Name", value.Name);
            }

            public void Deserialize(
                ref NullSerializationTests.TestClass value,
                ISerializationReader reader
            )
            {
                if (value == null)
                    value = new NullSerializationTests.TestClass();

                int tempValue = 0;
                reader.Read("Value", ref tempValue);
                value.Value = tempValue;
                value.Name = reader.ReadString("Name");
            }
        }
    }
}
