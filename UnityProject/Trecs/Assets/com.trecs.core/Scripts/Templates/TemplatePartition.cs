namespace Trecs
{
    /// <summary>
    /// Pairs a <see cref="Template"/> with an optional partition <see cref="TagSet"/>, identifying
    /// the specific group an entity should be created in. When <see cref="Partition"/> is null,
    /// the template's default (unpartitioned) group is used. Passed to
    /// <see cref="WorldAccessor.AddEntity{T}"/> overloads.
    /// </summary>
    public readonly struct TemplatePartition
    {
        public readonly Template Template;
        public readonly TagSet Partition;

        public TemplatePartition(Template template)
        {
            Template = template;
            Partition = TagSet.Null;
        }

        public TemplatePartition(Template template, TagSet partition)
        {
            Template = template;
            Partition = partition;
        }

        public override string ToString()
        {
            if (Partition.IsNull)
            {
                return Template.ToString();
            }

            return $"{Template} (partition: {Partition})";
        }

        public static implicit operator TemplatePartition(Template template)
        {
            return new TemplatePartition(template);
        }
    }
}
