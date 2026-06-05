using System;
using System.ComponentModel;
using Trecs.Internal;

namespace Trecs
{
    public interface IResolvedComponentDeclaration
    {
        Type ComponentType { get; }

        bool VariableUpdateOnly { get; }
        bool IsInput { get; }
        MissingInputBehavior? MissingInputBehavior { get; }
        bool IsConstant { get; }
        bool IsInterpolated { get; }
        bool HasDefault { get; }
        IComponentBuilder Builder { get; }

        // Note that this causes a boxing allocation when non null
        object TryGetDefaultValue();
    }
}

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal sealed class ResolvedComponentDeclaration<T>
        : IResolvedComponentDeclaration,
            IRemovalHandlerCollectable
        where T : unmanaged, IEntityComponent
    {
        static readonly Type _componentType = typeof(T);

        // The component's generated [CascadeRemove]/[DisposeOnRemove] handler
        // implementation, or null when T carries neither attribute (the
        // generator emits an explicit IComponentRemovalHandlers implementation
        // only on annotated partial component structs). Resolved once per closed
        // component type T in the static ctor — boxing default(T) here, rather
        // than in CollectRemovalHandlers, keeps the per-(group, component)
        // world-build precompute allocation-free. Safe to share one boxed
        // instance: the generated handler is stateless (its lambdas are static
        // and capture nothing), so it never reads this default(T)'s fields.
        static readonly IComponentRemovalHandlers _removalHandlers =
            default(T) as IComponentRemovalHandlers;

        // Dispatch to the component's generated [CascadeRemove]/[DisposeOnRemove]
        // handlers, if any. Called once per (group, component) during the
        // world-build precompute; a no-op for non-annotated components.
        public void CollectRemovalHandlers(RemovalHandlerCollector collector)
        {
            _removalHandlers?.RegisterRemovalHandlers(collector);
        }

        public ResolvedComponentDeclaration(
            bool variableUpdateOnly,
            bool isInput,
            MissingInputBehavior? inputFrameBehaviour,
            bool isConstant,
            T? defaultValue,
            bool isInterpolated
        )
        {
            VariableUpdateOnly = variableUpdateOnly;
            IsInput = isInput;
            MissingInputBehavior = inputFrameBehaviour;
            IsConstant = isConstant;
            IsInterpolated = isInterpolated;
            Default = defaultValue;
            Builder = new ComponentBuilder<T>(defaultValue);
        }

        public bool VariableUpdateOnly { get; }
        public bool IsInput { get; }
        public T? Default { get; }
        public MissingInputBehavior? MissingInputBehavior { get; }
        public bool IsConstant { get; }
        public bool IsInterpolated { get; }
        public IComponentBuilder Builder { get; }
        public Type ComponentType => _componentType;

        public object TryGetDefaultValue()
        {
            if (Default.HasValue)
            {
                return Default.Value;
            }

            return null;
        }

        public bool HasDefault
        {
            get { return Default.HasValue; }
        }
    }
}
