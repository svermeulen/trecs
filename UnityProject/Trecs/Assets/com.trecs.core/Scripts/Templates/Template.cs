using System.Collections.Generic;

namespace Trecs
{
    /// <summary>
    /// Declares an entity type's component layout, tags, valid states, and inheritance chain.
    /// Templates are defined as classes implementing <see cref="ITemplate"/> — the source generator
    /// produces concrete <see cref="Template"/> instances automatically. Register via
    /// <see cref="WorldBuilder.AddEntityType"/>.
    /// </summary>
    public class Template
    {
        public Template(
            string debugName,
            IReadOnlyList<Template> localBaseTemplates,
            IReadOnlyList<TagSet> states,
            IReadOnlyList<IComponentDeclaration> localComponentDeclarations,
            IReadOnlyList<Tag> localTags
        )
        {
            DebugName = debugName;
            LocalBaseTemplates = localBaseTemplates;
            LocalComponentDeclarations = localComponentDeclarations;
            LocalTags = localTags;
            States = states;
        }

        public override string ToString()
        {
            return DebugName;
        }

        /// <summary>
        /// Valid tag combinations this template can transition between.
        /// An empty list means the template has a single implicit state.
        /// </summary>
        public IReadOnlyList<TagSet> States { get; }

        /// <summary>
        /// Tags declared directly on this template (not inherited from base templates).
        /// </summary>
        public IReadOnlyList<Tag> LocalTags { get; }

        public string DebugName { get; }

        /// <summary>
        /// Direct parent templates this template extends (not transitive ancestors).
        /// </summary>
        public IReadOnlyList<Template> LocalBaseTemplates { get; }

        /// <summary>
        /// Component declarations defined directly on this template (not inherited).
        /// </summary>
        public IReadOnlyList<IComponentDeclaration> LocalComponentDeclarations { get; }
    }
}
