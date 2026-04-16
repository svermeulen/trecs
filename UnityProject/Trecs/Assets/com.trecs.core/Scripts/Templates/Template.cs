using System.Collections.Generic;

namespace Trecs
{
    /// <summary>
    /// Declares an entity type's component layout, tags, valid partitions, and inheritance chain.
    /// Templates are defined as classes implementing <see cref="ITemplate"/> — the source generator
    /// produces concrete <see cref="Template"/> instances automatically. Register via
    /// <see cref="WorldBuilder.AddEntityType"/>.
    /// </summary>
    public class Template
    {
        public Template(
            string debugName,
            IReadOnlyList<Template> localBaseTemplates,
            IReadOnlyList<TagSet> partitions,
            IReadOnlyList<IComponentDeclaration> localComponentDeclarations,
            IReadOnlyList<Tag> localTags
        )
        {
            DebugName = debugName;
            LocalBaseTemplates = localBaseTemplates;
            LocalComponentDeclarations = localComponentDeclarations;
            LocalTags = localTags;
            Partitions = partitions;
        }

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
