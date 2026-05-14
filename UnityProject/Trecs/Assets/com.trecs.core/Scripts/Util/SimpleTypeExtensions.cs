using System;
using System.Linq;
using System.Reflection;

namespace Trecs.Internal
{
    // Leave this here instead of unity util since it's used in zenject
    public static class SimpleTypeExtensions
    {
        public static bool HasAttribute<T>(this ICustomAttributeProvider provider)
            where T : Attribute
        {
            return provider.GetCustomAttributes(typeof(T), true).Length > 0;
        }

        public static bool HasAttribute(this ICustomAttributeProvider provider, Type attributeType)
        {
            TrecsAssert.That(attributeType.IsSubclassOf(typeof(Attribute)));
            return provider.GetCustomAttributes(attributeType, true).Length > 0;
        }

        public static T TryGetAttribute<T>(this ICustomAttributeProvider provider)
            where T : Attribute
        {
            return provider.GetCustomAttributes(typeof(T), true).Cast<T>().OnlyOrDefault();
        }

        public static T GetAttribute<T>(this ICustomAttributeProvider provider)
            where T : Attribute
        {
            return provider.GetCustomAttributes(typeof(T), true).Cast<T>().Single();
        }
    }
}
