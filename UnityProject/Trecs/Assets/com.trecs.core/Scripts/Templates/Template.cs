using System.Collections.Generic;

namespace Trecs
{
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

        public IReadOnlyList<TagSet> States { get; }

        public IReadOnlyList<Tag> LocalTags { get; }

        public string DebugName { get; }

        public IReadOnlyList<Template> LocalBaseTemplates { get; }

        public IReadOnlyList<IComponentDeclaration> LocalComponentDeclarations { get; }
    }
}
