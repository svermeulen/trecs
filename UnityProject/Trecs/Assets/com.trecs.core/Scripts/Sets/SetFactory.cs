using System;
using System.Collections.Generic;
using System.Reflection;
using Trecs.Internal;

namespace Trecs
{
    public static class SetFactory
    {
        // Track all created set IDs to detect hash collisions
        static readonly Dictionary<SetId, Type> _registeredSetIds = new();

        static readonly Type[] EntitySetGenericDefs =
        {
            typeof(IEntitySet<>),
            typeof(IEntitySet<,>),
            typeof(IEntitySet<,,>),
            typeof(IEntitySet<,,,>),
        };

        public static EntitySet CreateSet(Type setType)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            TrecsDebugAssert.That(
                typeof(IEntitySet).IsAssignableFrom(setType),
                "Set type {0} must implement IEntitySet",
                setType.FullName
            );

            TrecsDebugAssert.That(
                setType.IsValueType,
                "Set type {0} must be a struct, not a class",
                setType.FullName
            );

            var setId = new SetId(TypeId.FromType(setType));

            if (_registeredSetIds.TryGetValue(setId, out var existingType))
            {
                TrecsDebugAssert.That(
                    existingType == setType,
                    "Set ID collision: {0} and {1} both resolve to ID {2}. Use [SetId] to assign explicit IDs.",
                    setType.FullName,
                    existingType.FullName,
                    setId
                );
            }
            else
            {
                _registeredSetIds.Add(setId, setType);
            }

            TagSet tags = ExtractTags(setType);

            return new EntitySet(setId, tags, setType.Name, setType);
        }

        static TagSet ExtractTags(Type setType)
        {
            foreach (var iface in setType.GetInterfaces())
            {
                if (!iface.IsGenericType)
                {
                    continue;
                }

                var genDef = iface.GetGenericTypeDefinition();
                bool isEntitySet = false;

                for (int i = 0; i < EntitySetGenericDefs.Length; i++)
                {
                    if (genDef == EntitySetGenericDefs[i])
                    {
                        isEntitySet = true;
                        break;
                    }
                }

                if (!isEntitySet)
                {
                    continue;
                }

                var tagTypes = iface.GetGenericArguments();
                var tags = new Tag[tagTypes.Length];

                for (int i = 0; i < tagTypes.Length; i++)
                {
                    var tagGenericType = typeof(Tag<>).MakeGenericType(tagTypes[i]);
                    var valueProperty = tagGenericType.GetProperty(
                        "Value",
                        BindingFlags.Public | BindingFlags.Static
                    );

                    TrecsDebugAssert.That(
                        valueProperty != null,
                        "Tag type {0} does not have a static Value property",
                        tagTypes[i].FullName
                    );

                    tags[i] = (Tag)valueProperty.GetValue(null);
                }

                return TagSet.FromTags(tags);
            }

            // No generic IEntitySet<T> found — this is a global set valid for all groups
            return TagSet.Null;
        }
    }
}
