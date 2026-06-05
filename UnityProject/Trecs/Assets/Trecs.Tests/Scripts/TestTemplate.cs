using System.Collections.Generic;
using Trecs.Internal;

namespace Trecs.Tests
{
    /// <summary>
    /// Fluent builder for hand-authored <see cref="Template"/>s in tests.
    /// <para>
    /// Production code defines templates declaratively via <c>ITemplate</c> and the
    /// source generator, but tests frequently need ad-hoc templates. The raw
    /// <see cref="Template"/> constructor and <see cref="ComponentDeclaration{T}"/>
    /// (which takes six positional, mostly-null arguments) are too noisy for that —
    /// this builder collapses the common cases to a single readable line:
    /// </para>
    /// <code>
    /// TestTemplate.Named("Foo").WithTags(TestTags.Alpha).WithComponent&lt;TestInt&gt;();
    /// </code>
    /// <para>
    /// Implicitly converts to <see cref="Template"/>, so an instance can be passed
    /// straight to <see cref="EcsTestHelper.CreateEnvironment(Template[])"/> or
    /// <see cref="WorldBuilder.AddTemplate"/>. The conversion is memoized, so a
    /// builder used both as a base template and as a value yields the same
    /// <see cref="Template"/> reference.
    /// </para>
    /// <para>
    /// Tests that exercise the <see cref="Template"/> / <see cref="ComponentDeclaration{T}"/>
    /// constructors themselves should keep using those constructors directly — this
    /// builder is for incidental setup, not for the unit under test.
    /// </para>
    /// </summary>
    public sealed class TestTemplate
    {
        readonly string _debugName;
        readonly List<Template> _baseTemplates = new();
        readonly List<TagSet> _partitions = new();
        readonly List<IComponentDeclaration> _components = new();
        readonly List<Tag> _tags = new();
        readonly List<TagSet> _dimensions = new();
        bool _variableUpdateOnly;
        bool _isAbstract;

        Template _built;

        TestTemplate(string debugName)
        {
            _debugName = debugName;
        }

        public static TestTemplate Named(string debugName) => new TestTemplate(debugName);

        public TestTemplate Extending(params Template[] baseTemplates)
        {
            _baseTemplates.AddRange(baseTemplates);
            return this;
        }

        public TestTemplate WithTags(params Tag[] tags)
        {
            _tags.AddRange(tags);
            return this;
        }

        public TestTemplate WithPartitions(params TagSet[] partitions)
        {
            _partitions.AddRange(partitions);
            return this;
        }

        public TestTemplate WithDimensions(params TagSet[] dimensions)
        {
            _dimensions.AddRange(dimensions);
            return this;
        }

        public TestTemplate VariableUpdateOnly()
        {
            _variableUpdateOnly = true;
            return this;
        }

        public TestTemplate Abstract()
        {
            _isAbstract = true;
            return this;
        }

        /// <summary>
        /// Adds a component declaration. All declaration options default to "unset"
        /// (null), matching the most common <c>new ComponentDeclaration&lt;T&gt;(null, …)</c>
        /// usage; pass only the named arguments a given test actually cares about.
        /// </summary>
        public TestTemplate WithComponent<T>(
            T? defaultValue = null,
            bool? variableUpdateOnly = null,
            bool? isInput = null,
            MissingInputBehavior? missingInputBehavior = null,
            bool? isConstant = null,
            bool? isInterpolated = null
        )
            where T : unmanaged, IEntityComponent
        {
            _components.Add(
                new ComponentDeclaration<T>(
                    variableUpdateOnly,
                    isInput,
                    missingInputBehavior,
                    isConstant,
                    isInterpolated,
                    defaultValue
                )
            );
            return this;
        }

        public Template Build()
        {
            return _built ??= new Template(
                debugName: _debugName,
                localBaseTemplates: _baseTemplates.ToArray(),
                partitions: _partitions.ToArray(),
                localComponentDeclarations: _components.ToArray(),
                localTags: _tags.ToArray(),
                localVariableUpdateOnly: _variableUpdateOnly,
                dimensions: _dimensions.Count > 0 ? _dimensions.ToArray() : null,
                isAbstract: _isAbstract
            );
        }

        public static implicit operator Template(TestTemplate builder) => builder.Build();
    }
}
