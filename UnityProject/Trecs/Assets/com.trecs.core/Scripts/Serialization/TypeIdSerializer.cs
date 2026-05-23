using System;
using System.IO;
using Trecs.Internal;

namespace Trecs
{
    public static class TypeIdSerializer
    {
        public static void Write(Type objectType, BinaryWriter writer)
        {
            if (objectType.DerivesFrom<Type>())
            {
                objectType = typeof(Type);
            }

            TrecsDebugAssert.That(!objectType.IsGenericTypeDefinition);
            writer.Write(TypeId.FromType(objectType).Value);
        }

        public static Type Read(BinaryReader reader)
        {
            int id = reader.ReadInt32();
            return TypeId.ToType(new TypeId(id));
        }
    }
}
