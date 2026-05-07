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
        bool WarnOnMissingInput { get; }
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
    public class ResolvedComponentDeclaration<T> : IResolvedComponentDeclaration
        where T : unmanaged, IEntityComponent
    {
        static readonly Type _componentType = typeof(T);

        public ResolvedComponentDeclaration(
            bool variableUpdateOnly,
            bool isInput,
            MissingInputBehavior? inputFrameBehaviour,
            bool warnOnMissingInput,
            bool isConstant,
            T? defaultValue,
            bool isInterpolated
        )
        {
            VariableUpdateOnly = variableUpdateOnly;
            IsInput = isInput;
            MissingInputBehavior = inputFrameBehaviour;
            WarnOnMissingInput = warnOnMissingInput;
            IsConstant = isConstant;
            IsInterpolated = isInterpolated;
            Default = defaultValue;
            Builder = new ComponentBuilder<T>(defaultValue);
        }

        public bool VariableUpdateOnly { get; }
        public bool IsInput { get; }
        public T? Default { get; }
        public MissingInputBehavior? MissingInputBehavior { get; }
        public bool WarnOnMissingInput { get; }
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
