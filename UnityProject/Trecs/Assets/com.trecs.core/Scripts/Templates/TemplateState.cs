namespace Trecs
{
    /// <summary>
    /// Pairs a <see cref="Template"/> with an optional state <see cref="TagSet"/>, identifying
    /// the specific group an entity should be created in. When <see cref="State"/> is null,
    /// the template's default (stateless) group is used. Passed to
    /// <see cref="WorldAccessor.AddEntity{T}"/> overloads.
    /// </summary>
    public readonly struct TemplateState
    {
        public readonly Template Template;
        public readonly TagSet State;

        public TemplateState(Template template)
        {
            Template = template;
            State = TagSet.Null;
        }

        public TemplateState(Template template, TagSet state)
        {
            Template = template;
            State = state;
        }

        public override string ToString()
        {
            if (State.IsNull)
            {
                return Template.ToString();
            }

            return $"{Template} (state: {State})";
        }

        public static implicit operator TemplateState(Template template)
        {
            return new TemplateState(template);
        }
    }
}
