using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Trecs.Internal;

namespace Trecs
{
    public static class TypeIdSerializer
    {
        public static void Write(Type objectType, IBufferWriter<byte> writer)
        {
            if (objectType.DerivesFrom<Type>())
            {
                objectType = typeof(Type);
            }

            TrecsDebugAssert.That(!objectType.IsGenericTypeDefinition);
            var span = writer.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(span, TypeId.FromType(objectType).Value);
            writer.Advance(sizeof(int));
        }

        public static Type Read(MemoryStream stream)
        {
            int id = 0;
            MemoryBlitter.Read(ref id, stream);
            return TypeId.ToType(new TypeId(id));
        }
    }
}
