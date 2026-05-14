using System.Collections.Generic;
using Trecs.Internal;
using Unity.Mathematics;
using UnityEngine;

namespace Trecs.Tests
{
    public static class TestSerializerInstaller
    {
        public static void RegisterTestCollectionSerializers(SerializerRegistry registry)
        {
            // Collection serializers for testing
            registry.RegisterSerializer<ListSerializer<int>>();
            registry.RegisterSerializer<ListSerializer<string>>();
            registry.RegisterSerializer<DictionarySerializer<string, int>>();
            registry.RegisterSerializer<DictionarySerializer<int, string>>();
            registry.RegisterSerializer<DictionarySerializer<string, string>>();
            registry.RegisterSerializer<DictionarySerializer<int, int>>();
            registry.RegisterSerializer<DictionarySerializer<string, List<int>>>();
            registry.RegisterSerializer<HashSetSerializer<int>>();
            registry.RegisterSerializer<HashSetSerializer<string>>();

            // Array serializers for malformed collection tests
            registry.RegisterSerializer<ArraySerializer<int[], int>>();
            registry.RegisterSerializer<ArraySerializer<string[], string>>();

            // Nested collection serializers for malformed tests
            registry.RegisterSerializer<ListSerializer<List<int>>>();

            // Test class serializer for null serialization tests (the JSON
            // counterpart lives in JsonNullSerializationTests itself so the
            // public exporter can drop it together with the JSON tests).
            registry.RegisterSerializer<TestClassSerializer>();

            // FastList blit serializers for performance testing
            registry.RegisterSerializer<FastListSerializer<int>>();
            registry.RegisterSerializer<FastListSerializer<float>>();
            registry.RegisterSerializer<FastListSerializer<Vector3>>();
            registry.RegisterSerializer<FastListSerializer<int2>>();
            registry.RegisterSerializer<FastListSerializer<byte>>();

            // DenseDictionary serializers for blit performance testing
            registry.RegisterSerializer<DenseDictionarySerializer<int, float>>();
            registry.RegisterSerializer<DenseDictionarySerializer<int, int>>();
            registry.RegisterSerializer<DenseDictionarySerializer<int, Vector3>>();
            registry.RegisterSerializer<DenseDictionarySerializer<int2, float3>>();
            registry.RegisterSerializer<DenseDictionarySerializer<int, string>>();
        }

        public static SerializerRegistry CreateTestRegistry()
        {
            var registry = new SerializerRegistry();
            RegisterTestCollectionSerializers(registry);
            return registry;
        }

        public static SerializerRegistry CreateJsonTestRegistry()
        {
            return new SerializerRegistry();
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
