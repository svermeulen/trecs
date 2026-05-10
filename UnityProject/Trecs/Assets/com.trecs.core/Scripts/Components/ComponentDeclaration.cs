using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Trecs
{
    public interface IComponentDeclaration
    {
        Type ComponentType { get; }

        bool? VariableUpdateOnly { get; }
        bool? IsInput { get; }
        MissingInputBehavior? MissingInputBehavior { get; }
        bool? IsConstant { get; }
        bool HasDefault { get; }

        List<IResolvedComponentDeclaration> MergeAsConcrete(
            List<IComponentDeclaration> declarations,
            string templateContext
        );
    }
}

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class ComponentDeclaration<T> : IComponentDeclaration
        where T : unmanaged, IEntityComponent
    {
        static readonly Type _componentType = typeof(T);

        public ComponentDeclaration(
            bool? variableUpdateOnly,
            bool? isInput,
            MissingInputBehavior? inputFrameBehaviour,
            bool? isConstant,
            bool? isInterpolated,
            T? defaultValue
        )
        {
            VariableUpdateOnly = variableUpdateOnly;
            IsInput = isInput;
            MissingInputBehavior = inputFrameBehaviour;
            IsConstant = isConstant;
            IsInterpolated = isInterpolated;
            Default = defaultValue;
        }

        public bool? VariableUpdateOnly { get; }
        public bool? IsInput { get; }
        public T? Default { get; }
        public MissingInputBehavior? MissingInputBehavior { get; }
        public bool? IsConstant { get; }
        public bool? IsInterpolated { get; }
        public Type ComponentType => _componentType;

        public bool HasDefault
        {
            get { return Default.HasValue; }
        }

        public List<IResolvedComponentDeclaration> MergeAsConcrete(
            List<IComponentDeclaration> declarations,
            string templateContext
        )
        {
            Assert.That(declarations.Contains(this));

            bool? variableUpdateOnly = null;
            bool? isInput = null;
            bool? isConstant = null;
            bool? isInterpolated = null;
            MissingInputBehavior? inputFrameBehaviour = null;
            T? defaultValue = null;

            foreach (var baseDec in declarations)
            {
                var dec = (ComponentDeclaration<T>)baseDec;

                if (dec.VariableUpdateOnly.HasValue)
                {
                    if (variableUpdateOnly.HasValue)
                    {
                        Assert.That(
                            variableUpdateOnly.Value == dec.VariableUpdateOnly.Value,
                            "Found conflicting VariableUpdateOnly declarations for component type {} while processing template {}",
                            _componentType,
                            templateContext
                        );
                    }
                    else
                    {
                        variableUpdateOnly = dec.VariableUpdateOnly;
                    }
                }

                if (dec.IsInput.HasValue)
                {
                    if (isInput.HasValue)
                    {
                        Assert.That(
                            isInput.Value == dec.IsInput.Value,
                            "Found conflicting IsInput declarations for component type {} while processing template {}",
                            _componentType,
                            templateContext
                        );
                    }
                    else
                    {
                        isInput = dec.IsInput;
                    }
                }

                if (dec.IsConstant.HasValue)
                {
                    if (isConstant.HasValue)
                    {
                        Assert.That(
                            isConstant.Value == dec.IsConstant.Value,
                            "Found conflicting IsConstant declarations for component type {} while processing template {}",
                            _componentType,
                            templateContext
                        );
                    }
                    else
                    {
                        isConstant = dec.IsConstant;
                    }
                }

                if (dec.IsInterpolated.HasValue)
                {
                    if (isInterpolated.HasValue)
                    {
                        Assert.That(
                            isInterpolated.Value == dec.IsInterpolated.Value,
                            "Found conflicting IsInterpolated declarations for component type {} while processing template {}",
                            _componentType,
                            templateContext
                        );
                    }
                    else
                    {
                        isInterpolated = dec.IsInterpolated;
                    }
                }

                if (dec.MissingInputBehavior.HasValue)
                {
                    if (inputFrameBehaviour.HasValue)
                    {
                        Assert.That(
                            inputFrameBehaviour.Value == dec.MissingInputBehavior.Value,
                            "Found conflicting MissingInputBehavior declarations for component type {} while processing template {}",
                            _componentType,
                            templateContext
                        );
                    }
                    else
                    {
                        inputFrameBehaviour = dec.MissingInputBehavior;
                    }
                }

                if (dec.HasDefault)
                {
                    if (defaultValue.HasValue)
                    {
                        Assert.That(
                            UnmanagedUtil.BlittableEquals(dec.Default.Value, defaultValue.Value),
                            "Found multiple non-equal default values for component type {} while processing template {}",
                            _componentType,
                            templateContext
                        );
                    }
                    else
                    {
                        defaultValue = dec.Default;
                    }
                }
            }

            bool variableUpdateOnlyChoice = variableUpdateOnly ?? false;
            bool isInputChoice = isInput ?? false;
            bool isConstantChoice = isConstant ?? false;
            bool isInterpolatedChoice = isInterpolated ?? false;

            Assert.That(
                !isInterpolatedChoice || !isConstantChoice,
                "Component type {} cannot be both Interpolated and Constant while processing template {}",
                _componentType,
                templateContext
            );

            if (isInputChoice)
            {
                Assert.That(inputFrameBehaviour.HasValue);
                Assert.That(
                    !isConstantChoice,
                    "Component type {} cannot be both Input and Constant while processing template {}",
                    _componentType,
                    templateContext
                );

                // This actually is valid sometimes
                // In one case - we send camera transform as input, but then we want interpolated values for it which we
                // use when playing recording
                // Assert.That(
                //     !isInterpolatedChoice,
                //     "Component type {} cannot be both Input and Interpolated while processing template {}", _componentType, templateContext);

                Assert.That(
                    !variableUpdateOnlyChoice,
                    "Component type {} cannot be both Input and VariableUpdateOnly while processing template {}",
                    _componentType,
                    templateContext
                );
            }

            var result = new List<IResolvedComponentDeclaration>
            {
                new ResolvedComponentDeclaration<T>(
                    variableUpdateOnly: variableUpdateOnlyChoice,
                    isInput: isInputChoice,
                    inputFrameBehaviour: inputFrameBehaviour,
                    isConstant: isConstantChoice,
                    defaultValue: defaultValue,
                    isInterpolated: isInterpolatedChoice
                ),
            };

            if (isInterpolatedChoice)
            {
                Assert.That(
                    !variableUpdateOnlyChoice,
                    "Component type {} cannot be both Interpolated and VariableUpdateOnly while processing template {}",
                    _componentType,
                    templateContext
                );

                result.Add(
                    new ResolvedComponentDeclaration<Interpolated<T>>(
                        variableUpdateOnly: true,
                        isInput: false,
                        inputFrameBehaviour: null,
                        isConstant: false,
                        defaultValue: new(),
                        isInterpolated: false
                    )
                );

                result.Add(
                    new ResolvedComponentDeclaration<InterpolatedPrevious<T>>(
                        variableUpdateOnly: false,
                        isInput: false,
                        inputFrameBehaviour: null,
                        isConstant: false,
                        defaultValue: defaultValue.HasValue ? new(defaultValue.Value) : null,
                        isInterpolated: false
                    )
                );
            }

            return result;
        }
    }
}
