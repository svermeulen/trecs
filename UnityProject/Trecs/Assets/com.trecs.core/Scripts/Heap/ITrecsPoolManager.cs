using System;

namespace Trecs
{
    public interface ITrecsPoolManager
    {
        T Spawn<T>()
            where T : class;

        void Despawn(object value);
        void Despawn(Type type, object value);

        bool HasPool(Type type);
        bool HasPool<T>()
            where T : class;
    }
}
