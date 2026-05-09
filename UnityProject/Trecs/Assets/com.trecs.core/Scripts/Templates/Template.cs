using System.Collections.Generic;

namespace Trecs
{
    /// <summary>
    /// Describes an entity's component layout, tags, valid partitions, and inheritance chain.
    /// Templates are defined as classes implementing <see cref="ITemplate"/> — the source generator
    /// produces concrete <see cref="Template"/> instances automatically. Register via
    /// <see cref="WorldBuilder.AddTemplate"/>.
    /// </summary>
    public sealed class Template
    {
        public Template(
            string debugName,
            IReadOnlyList<Template> localBaseTemplates,
            IReadOnlyList<TagSet> partitions,
            IReadOnlyList<IComponentDeclaration> localComponentDeclarations,
            IReadOnlyList<Tag> localTags,
            bool localVariableUpdateOnly = false
        )
        {
            DebugName = debugName;
            LocalBaseTemplates = localBaseTemplates;
            LocalComponentDeclarations = localComponentDeclarations;
            LocalTags = localTags;
            Partitions = partitions;
            LocalVariableUpdateOnly = localVariableUpdateOnly;
        }

        /// <summary>
        /// True iff this exact template class is declared <c>[VariableUpdateOnly]</c>.
        /// Does NOT account for inheritance — a derived template that inherits VUO
        /// from a base will still report <c>false</c> here. Production access-rule
        /// code should read <see cref="ResolvedTemplate.VariableUpdateOnly"/> instead,
        /// which transitively ORs the flag across the full base-template chain.
        /// </summary>
        public bool LocalVariableUpdateOnly { get; }

        public override string ToString()
        {
            return DebugName;
        }

        /// <summary>
        /// Valid partition tag combinations this template can be placed in.
        /// An empty list means the template has a single implicit partition.
        /// </summary>
        public IReadOnlyList<TagSet> Partitions { get; }

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
