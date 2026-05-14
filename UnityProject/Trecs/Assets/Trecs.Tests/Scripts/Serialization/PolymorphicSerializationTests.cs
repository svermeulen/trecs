using System;
using NUnit.Framework;
using Trecs.Internal;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class PolymorphicSerializationTests
    {
        private SerializerRegistry _serializerRegistry;
        private SerializationBuffer _cacheHelper;

        [SetUp]
        public void SetUp()
        {
            _serializerRegistry = TestSerializerInstaller.CreateTestRegistry();

            // Register polymorphic types (concrete classes only - framework doesn't support interfaces)
            _serializerRegistry.RegisterSerializer<BaseClassSerializer>();
            _serializerRegistry.RegisterSerializer<DerivedClassSerializer>();
            _cacheHelper = new SerializationBuffer(_serializerRegistry);
        }

        [TearDown]
        public void TearDown()
        {
            _cacheHelper?.Dispose();
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAll(original, TestConstants.Version, includeTypeChecks: true, flags);
            _cacheHelper.ResetMemoryPosition();
            var result = _cacheHelper.ReadAll<DerivedTestClass>();

            // Assert
            Assert.IsNotNull(result);
            Assert.That(result.BaseValue == 42);
            Assert.That(result.BaseData == "test");
            Assert.That(result.DerivedValue == 100);
            Assert.That(result.ExtraData == "extra");
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
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllObject(
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var resultObj = _cacheHelper.ReadAllObject();
            var result = resultObj as BaseTestClass;

            // Assert
            Assert.IsNotNull(result);
            Assert.That(result is DerivedTestClass);
            var derived = result as DerivedTestClass;
            Assert.That(derived.BaseValue == 100);
            Assert.That(derived.DerivedValue == 200);
            Assert.That(derived.ExtraData == "derived");
        }

        [Test]
        public void BaseClass_BaseInstance_SerializesCorrectly()
        {
            // Arrange
            BaseTestClass original = new BaseTestClass { BaseValue = 50, BaseData = "base" };
            var flags = 0L;

            // Act - Use WriteAllObject for polymorphic serialization
            _cacheHelper.ClearMemoryStream();
            _cacheHelper.WriteAllObject(
                original,
                TestConstants.Version,
                includeTypeChecks: true,
                flags
            );
            _cacheHelper.ResetMemoryPosition();
            var resultObj = _cacheHelper.ReadAllObject();
            var result = resultObj as BaseTestClass;

            // Assert
            Assert.IsNotNull(result);
            Assert.That(result.GetType() == typeof(BaseTestClass));
            Assert.That(result.BaseValue == 50);
            Assert.That(result.BaseData == "base");
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
                writer.WriteTypeId("type", value.GetType());
                writer.Write("baseValue", value.BaseValue);
                writer.WriteString("baseData", value.BaseData);

                if (value is DerivedTestClass derived)
                {
                    writer.Write("derivedValue", derived.DerivedValue);
                    writer.WriteString("extraData", derived.ExtraData);
                }
            }

            public void Deserialize(ref BaseTestClass value, ISerializationReader reader)
            {
                var type = reader.ReadTypeId("type");
                var baseValue = reader.Read<int>("baseValue");
                var baseData = reader.ReadString("baseData");

                if (type == typeof(BaseTestClass))
                {
                    value = new BaseTestClass { BaseValue = baseValue, BaseData = baseData };
                }
                else if (type == typeof(DerivedTestClass))
                {
                    var derivedValue = reader.Read<int>("derivedValue");
                    var extraData = reader.ReadString("extraData");

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
                writer.Write("baseValue", value.BaseValue);
                writer.WriteString("baseData", value.BaseData);
                writer.Write("derivedValue", value.DerivedValue);
                writer.WriteString("extraData", value.ExtraData);
            }

            public void Deserialize(ref DerivedTestClass value, ISerializationReader reader)
            {
                value = new DerivedTestClass
                {
                    BaseValue = reader.Read<int>("baseValue"),
                    BaseData = reader.ReadString("baseData"),
                    DerivedValue = reader.Read<int>("derivedValue"),
                    ExtraData = reader.ReadString("extraData"),
                };
            }
        }
    }
}
