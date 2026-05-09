using System;
using System.ComponentModel;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface IComponentBuilder
    {
        void BuildEntityAndAddToList(IComponentArray dictionary);
        void Preallocate(IComponentArray dictionary, int size);
        IComponentArray CreateDictionary(int size);

        void ResetInputs(EntityInputQueue inputManager, GroupIndex group);

        bool HasUserProvidedPrototype { get; }

        ComponentId ComponentId { get; }

        Type ComponentType { get; }
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class ComponentBuilder<T> : IComponentBuilder
        where T : unmanaged, IEntityComponent
    {
        static readonly Type _componentType;

        static ComponentBuilder()
        {
            _componentType = typeof(T);
            // Important: serves as warm up for TypeMeta<T>
            _ = TypeMeta<T>.IsUnmanaged;
            if (TypeMeta<T>.IsUnmanaged)
                EntityComponentIdMap.Register<T>(new Filler<T>());
            ComponentTypeId<T>.Init();
        }

        public ComponentBuilder(T? prototype)
        {
            if (prototype.HasValue)
            {
                _prototype = prototype.Value;
                _hasUserProvidedPrototype = true;
            }
            else
            {
                _prototype = default;
                _hasUserProvidedPrototype = false;
            }
        }

        public bool HasUserProvidedPrototype
        {
            get { return _hasUserProvidedPrototype; }
        }

        public ComponentId ComponentId => ComponentTypeId<T>.Value;

        public void BuildEntityAndAddToList(IComponentArray dictionary)
        {
            var castedDic = dictionary as IComponentArray<T>;

            castedDic.Add(_prototype);
        }

        void IComponentBuilder.Preallocate(IComponentArray dictionary, int size)
        {
            Preallocate(dictionary, size);
        }

        public IComponentArray CreateDictionary(int size)
        {
            return new ComponentArray<T>(size);
        }

        public void ResetInputs(EntityInputQueue inputQueue, GroupIndex group)
        {
            inputQueue.ResetInputs<T>(group);
        }

        public Type ComponentType
        {
            get { return _componentType; }
        }

        static void Preallocate(IComponentArray dictionary, int size)
        {
            dictionary.EnsureCapacity(size);
        }

        readonly T _prototype;
        readonly bool _hasUserProvidedPrototype;
    }
}
