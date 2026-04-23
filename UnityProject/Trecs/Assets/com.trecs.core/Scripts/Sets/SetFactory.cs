using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Trecs.Collections;
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

        public static SetDef CreateSet(Type setType)
        {
            Assert.That(UnityThreadHelper.IsMainThread);
            Assert.That(
                typeof(IEntitySet).IsAssignableFrom(setType),
                "Set type {} must implement IEntitySet",
                setType.FullName
            );

            Assert.That(
                setType.IsValueType,
                "Set type {} must be a struct, not a class",
                setType.FullName
            );

            var setId = new SetId(ComputeSetId(setType));
            Assert.That(setId.Id != 0, "Set ID must not be zero for type {}", setType.FullName);

            if (_registeredSetIds.TryGetValue(setId, out var existingType))
            {
                Assert.That(
                    existingType == setType,
                    "Set ID collision: {} and {} both resolve to ID {}. Use [SetId] to assign explicit IDs.",
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

            return new SetDef(setId, tags, setType.Name);
        }

        static int ComputeSetId(Type setType)
        {
            if (
                setType.GetCustomAttributes(typeof(SetIdAttribute), false).FirstOrDefault()
                is SetIdAttribute idAttr
            )
            {
                return idAttr.Id;
            }

            var id = DenseHashUtil.StableStringHash(setType.FullName);

            if (id == 0)
            {
                id = 1;
            }

            return id;
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

                    Assert.That(
                        valueProperty != null,
                        "Tag type {} does not have a static Value property",
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
