using System;
using NUnit.Framework;
using Trecs.Internal;
using NAssert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class PolymorphicSerializationTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationHelper _helper;
        private SerializationData _data;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();

            // Register polymorphic types (concrete classes only - framework doesn't support interfaces)
            _serializerRegistry.RegisterSerializer(new BaseClassSerializer());
            _serializerRegistry.RegisterSerializer(new DerivedClassSerializer());
            _helper = new SerializationHelper(_serializerRegistry);
            _data = new SerializationData();
        }

        [Test]
        public void ConcreteClass_SerializesDirectly()
        {
            // Arrange - Test direct serialization of concrete class
            var original = new DerivedTestClass
            {
                BaseValue = 42,
                BaseData = "test",
                DerivedValue = 100,
                ExtraData = "extra",
            };
            var flags = 0L;

            // Act
            _helper.WriteAll(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var result = _helper.ReadAll<DerivedTestClass>(_data);

            // Assert
            NAssert.IsNotNull(result);
            NAssert.That(result.BaseValue == 42);
            NAssert.That(result.BaseData == "test");
            NAssert.That(result.DerivedValue == 100);
            NAssert.That(result.ExtraData == "extra");
        }

        [Test]
        public void BaseClass_DerivedInstance_SerializesCorrectly()
        {
            // Arrange
            BaseTestClass original = new DerivedTestClass
            {
                BaseValue = 100,
                DerivedValue = 200,
                ExtraData = "derived",
            };
            var flags = 0L;

            // Act - Use WriteAllObject for polymorphic serialization
            _helper.WriteAllObject(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var resultObj = _helper.ReadAllObject(_data);
            var result = resultObj as BaseTestClass;

            // Assert
            NAssert.IsNotNull(result);
            NAssert.That(result is DerivedTestClass);
            var derived = result as DerivedTestClass;
            NAssert.That(derived.BaseValue == 100);
            NAssert.That(derived.DerivedValue == 200);
            NAssert.That(derived.ExtraData == "derived");
        }

        [Test]
        public void BaseClass_BaseInstance_SerializesCorrectly()
        {
            // Arrange
            BaseTestClass original = new BaseTestClass { BaseValue = 50, BaseData = "base" };
            var flags = 0L;

            // Act - Use WriteAllObject for polymorphic serialization
            _helper.WriteAllObject(
                _data,
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            var resultObj = _helper.ReadAllObject(_data);
            var result = resultObj as BaseTestClass;

            // Assert
            NAssert.IsNotNull(result);
            NAssert.That(result.GetType() == typeof(BaseTestClass));
            NAssert.That(result.BaseValue == 50);
            NAssert.That(result.BaseData == "base");
        }

        // Test classes for polymorphic serialization
        public class BaseTestClass
        {
            public int BaseValue { get; set; }
            public string BaseData { get; set; }
        }

        public class DerivedTestClass : BaseTestClass
        {
            public int DerivedValue { get; set; }
            public string ExtraData { get; set; }
        }

        // Serializers for polymorphic types
        public class BaseClassSerializer : ISerializer<BaseTestClass>
        {
            public BaseClassSerializer() { }

            public void Serialize(in BaseTestClass value, ISerializationWriter writer)
            {
                writer.WriteTypeId("Type", value.GetType());
                writer.Write("BaseValue", value.BaseValue);
                writer.WriteString("BaseData", value.BaseData);

                if (value is DerivedTestClass derived)
                {
                    writer.Write("DerivedValue", derived.DerivedValue);
                    writer.WriteString("ExtraData", derived.ExtraData);
                }
            }

            public void Deserialize(ref BaseTestClass value, ISerializationReader reader)
            {
                var type = reader.ReadTypeId("Type");
                var baseValue = reader.Read<int>("BaseValue");
                var baseData = reader.ReadString("BaseData");

                if (type == typeof(BaseTestClass))
                {
                    value = new BaseTestClass { BaseValue = baseValue, BaseData = baseData };
                }
                else if (type == typeof(DerivedTestClass))
                {
                    var derivedValue = reader.Read<int>("DerivedValue");
                    var extraData = reader.ReadString("ExtraData");

                    value = new DerivedTestClass
                    {
                        BaseValue = baseValue,
                        BaseData = baseData,
                        DerivedValue = derivedValue,
                        ExtraData = extraData,
                    };
                }
                else
                {
                    throw new NotSupportedException($"Unsupported type: {type}");
                }
            }
        }

        public class DerivedClassSerializer : ISerializer<DerivedTestClass>
        {
            public DerivedClassSerializer() { }

            public void Serialize(in DerivedTestClass value, ISerializationWriter writer)
            {
                writer.Write("BaseValue", value.BaseValue);
                writer.WriteString("BaseData", value.BaseData);
                writer.Write("DerivedValue", value.DerivedValue);
                writer.WriteString("ExtraData", value.ExtraData);
            }

            public void Deserialize(ref DerivedTestClass value, ISerializationReader reader)
            {
                value = new DerivedTestClass
                {
                    BaseValue = reader.Read<int>("BaseValue"),
                    BaseData = reader.ReadString("BaseData"),
                    DerivedValue = reader.Read<int>("DerivedValue"),
                    ExtraData = reader.ReadString("ExtraData"),
                };
            }
        }
    }
}
