using System;
using System.IO;
using Trecs.Internal;

namespace Trecs
{
    public static class TypeIdSerializer
    {
        public static void Write(Type objectType, BinaryWriter writer)
        {
            using var _ = TrecsProfiling.Start("WriteTypeId");

            if (objectType.DerivesFrom<Type>())
            {
                objectType = typeof(Type);
            }

            TrecsAssert.That(!objectType.IsGenericTypeDefinition);
            writer.Write(TypeIdProvider.GetTypeId(objectType));
        }

        public static Type Read(BinaryReader reader)
        {
            int id = reader.ReadInt32();
            return TypeIdProvider.GetTypeFromId(id);
        }
    }
}
