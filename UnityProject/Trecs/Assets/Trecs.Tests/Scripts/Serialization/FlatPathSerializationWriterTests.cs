using System;
using System.IO;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;

namespace Trecs.Tests
{
    [TestFixture]
    public class FlatPathSerializationWriterTests
    {
        SerializerRegistry _registry;
        StringWriter _stringWriter;
        FlatPathSerializationWriter _writer;

        struct TestStruct
        {
            public int X;
            public float Y;
        }

        class Outer
        {
            public int A;
            public Inner B;
        }

        class Inner
        {
            public string Name;
            public float Value;
        }

        class OuterSerializer : ISerializer<Outer>
        {
            public void Serialize(in Outer value, ISerializationWriter writer)
            {
                writer.Write("A", value.A);
                writer.Write("B", value.B);
            }

            public void Deserialize(ref Outer value, ISerializationReader reader)
            {
                throw new NotImplementedException();
            }
        }

        class InnerSerializer : ISerializer<Inner>
        {
            public void Serialize(in Inner value, ISerializationWriter writer)
            {
                writer.WriteString("Name", value.Name);
                writer.Write("Value", value.Value);
            }

            public void Deserialize(ref Inner value, ISerializationReader reader)
            {
                throw new NotImplementedException();
            }
        }

        [SetUp]
        public void SetUp()
        {
            _registry = TestSerializerInstaller.CreateTestRegistry();
            _stringWriter = new StringWriter();
            _writer = new FlatPathSerializationWriter(_stringWriter, _registry);
        }

        [TearDown]
        public void TearDown()
        {
            _stringWriter?.Dispose();
        }

        string Output() => _stringWriter.ToString();

        [Test]
        public void Start_EmitsVersionAndFlags()
        {
            _writer.Start(version: 7, flags: 0L);
            _writer.Complete();

            var output = Output();
            Assert.That(output, Does.Contain("Version = 7\n"));
            Assert.That(output, Does.Contain("Flags = 0\n"));
        }

        [Test]
        public void Write_Int_EmitsFlatPrimitiveLine()
        {
            _writer.Start(version: 1);
            _writer.Write("Health", 42);
            _writer.Complete();

            Assert.That(Output(), Does.Contain("Health = 42\n"));
        }

        [Test]
        public void BlitWrite_Struct_ExpandsFieldsUnderPath()
        {
            var value = new TestStruct { X = 3, Y = 2.5f };

            _writer.Start(version: 1);
            _writer.BlitWrite("Pos", value);
            _writer.Complete();

            var output = Output();
            Assert.That(output, Does.Contain("Pos.X = 3\n"));
            Assert.That(output, Does.Contain("Pos.Y = 2.5\n"));
        }

        [Test]
        public void BlitWriteArray_Struct_EmitsIndexedPathPerElement()
        {
            var arr = new[]
            {
                new TestStruct { X = 1, Y = 1.5f },
                new TestStruct { X = 9, Y = -2f },
            };

            _writer.Start(version: 1);
            _writer.BlitWriteArray("Items", arr, arr.Length);
            _writer.Complete();

            var output = Output();
            Assert.That(output, Does.Contain("Items[0].X = 1\n"));
            Assert.That(output, Does.Contain("Items[0].Y = 1.5\n"));
            Assert.That(output, Does.Contain("Items[1].X = 9\n"));
            Assert.That(output, Does.Contain("Items[1].Y = -2\n"));
        }

        [Test]
        public void WriteString_EscapesControlChars()
        {
            _writer.Start(version: 1);
            _writer.WriteString("Greeting", "Hello\nWorld");
            _writer.Complete();

            Assert.That(Output(), Does.Contain("Greeting = \"Hello\\nWorld\"\n"));
        }

        [Test]
        public void WriteString_Null_EmitsLiteralNull()
        {
            _writer.Start(version: 1);
            _writer.WriteString("Greeting", null);
            _writer.Complete();

            Assert.That(Output(), Does.Contain("Greeting = null\n"));
        }

        [Test]
        public void WriteBytes_NonEmpty_EmitsHashedSummary()
        {
            var bytes = new byte[] { 1, 2, 3, 4 };

            _writer.Start(version: 1);
            _writer.WriteBytes("Data", bytes, offset: 0, count: bytes.Length);
            _writer.Complete();

            var output = Output();
            Assert.That(output, Does.Contain("Data = bytes(len=4, sha256="));
        }

        [Test]
        public void WriteBytes_SameInput_ProducesSameHash()
        {
            var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

            _writer.Start(version: 1);
            _writer.WriteBytes("A", bytes, offset: 0, count: bytes.Length);
            _writer.WriteBytes("B", bytes, offset: 0, count: bytes.Length);
            _writer.Complete();

            // Same input must produce byte-identical lines — desync diff
            // depends on hash determinism.
            var output = Output();
            var lineA = ExtractLine(output, "A = ");
            var lineB = ExtractLine(output, "B = ");
            Assert.That(lineA.Substring(2), Is.EqualTo(lineB.Substring(2)));
        }

        static string ExtractLine(string output, string prefix)
        {
            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith(prefix))
                {
                    return line;
                }
            }
            return null;
        }

        [Test]
        public void WriteBytes_Empty_OmitsHash()
        {
            _writer.Start(version: 1);
            _writer.WriteBytes("Data", new byte[0], offset: 0, count: 0);
            _writer.Complete();

            Assert.That(Output(), Does.Contain("Data = bytes(len=0)\n"));
        }

        [Test]
        public void WriteBit_AutoIncrementsIndexedName()
        {
            _writer.Start(version: 1);
            _writer.WriteBit(true);
            _writer.WriteBit(false);
            _writer.WriteBit(true);
            _writer.Complete();

            var output = Output();
            Assert.That(output, Does.Contain("_b0 = true\n"));
            Assert.That(output, Does.Contain("_b1 = false\n"));
            Assert.That(output, Does.Contain("_b2 = true\n"));
        }

        // Exercises serializer recursion: Write<Outer> dispatches
        // OuterSerializer, whose Serialize calls writer.Write("A", ...) /
        // writer.Write("B", ...) on the writer it was handed. Because
        // FlatPathSerializationWriter implements ISerializationWriter
        // directly, recursion lands back here with the path stack nesting
        // correctly across both levels.
        [Test]
        public void Write_NestedObjects_BuildsDotPath()
        {
            _registry.RegisterSerializer(new OuterSerializer());
            _registry.RegisterSerializer(new InnerSerializer());

            var data = new Outer
            {
                A = 7,
                B = new Inner { Name = "alice", Value = 1.5f },
            };

            _writer.Start(version: 1);
            _writer.Write("Root", data);
            _writer.Complete();

            var output = Output();
            Assert.That(output, Does.Contain("Root.A = 7\n"));
            Assert.That(output, Does.Contain("Root.B.Name = \"alice\"\n"));
            Assert.That(output, Does.Contain("Root.B.Value = 1.5\n"));
        }
    }
}
