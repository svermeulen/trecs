namespace Trecs
{
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
