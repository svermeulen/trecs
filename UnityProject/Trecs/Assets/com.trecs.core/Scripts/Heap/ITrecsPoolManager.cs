using System;

namespace Trecs
{
    /// <summary>
    /// Object pool interface for recycling managed allocations. Supply a custom implementation
    /// to <see cref="WorldBuilder.SetPoolManager"/> to control how managed heap objects are
    /// allocated and returned.
    /// </summary>
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
