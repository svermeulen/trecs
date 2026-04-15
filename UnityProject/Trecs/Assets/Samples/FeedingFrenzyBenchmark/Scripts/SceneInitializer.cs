namespace Trecs.Samples.FeedingFrenzyBenchmark
{
    public partial class SceneInitializer
    {
        readonly CommonSettings _settings;
        readonly FrenzyConfigSettings _config;

        public SceneInitializer(CommonSettings settings, World world, FrenzyConfigSettings config)
        {
            _settings = settings;
            _config = config;

            World = world.CreateAccessor();
        }

        WorldAccessor World { get; }

        public void Initialize()
        {
            var globals = Globals.Query(World).WithTags<TrecsTags.Globals>().Single();

            globals.DesiredPreset = _settings.DefaultPresetIndex;
            globals.FrenzyConfig = new FrenzyConfig
            {
                StateApproach = _config.StateApproach,
                IterationStyle = _config.IterationStyle,
                Deterministic = _config.Deterministic,
            };
        }

        partial struct Globals : IAspect, IWrite<DesiredPreset, FrenzyConfig> { }
    }
}
