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

            // Unrestricted because the initial values for the [Input]-marked
            // DesiredPreset / DesiredIterationStyle fields come from runtime
            // settings rather than the template default. World.AddInput is
            // only callable from Input-phase systems, so pre-tick scene
            // setup uses the documented escape-hatch role instead.
            World = world.CreateAccessor(AccessorRole.Unrestricted);
        }

        WorldAccessor World { get; }

        public void Initialize()
        {
            var globals = Globals.Query(World).WithTags<TrecsTags.Globals>().Single();

            globals.DesiredPreset = _settings.DefaultPresetIndex;
            globals.DesiredIterationStyle = _config.IterationStyle;
            globals.FrenzyConfig = new FrenzyConfig
            {
                SubsetApproach = _config.SubsetApproach,
                IterationStyle = _config.IterationStyle,
                Deterministic = _config.Deterministic,
            };
        }

        partial struct Globals
            : IAspect,
                IWrite<DesiredPreset, DesiredIterationStyle, FrenzyConfig> { }
    }
}
