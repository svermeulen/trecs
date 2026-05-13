namespace Trecs.Serialization.Internal
{
    /// <summary>
    /// Escape hatch for code that needs a <see cref="SerializerRegistry"/>
    /// without a <see cref="World"/> available: static file-header peeks
    /// (e.g. <c>TrecsAutoRecorder.TryReadRecordingHeader</c>), unit tests,
    /// and DI installers that bind a singleton before any world is built.
    ///
    /// Registries created via this factory are NOT tracked in
    /// <see cref="TrecsSerializerRegistries"/>. Callers that want the Trecs
    /// Player window to find them must call
    /// <c>TrecsSerializerRegistries.Set</c> manually after a world exists.
    ///
    /// Lives in the <see cref="Trecs.Internal"/> namespace to signal that
    /// it's not part of the public Trecs API — end-user code should use
    /// <see cref="TrecsSerialization.CreateSerializerRegistry"/>.
    /// </summary>
    public static class SerializationFactory
    {
        public static SerializerRegistry CreateRegistry()
        {
            var registry = new SerializerRegistry();
            TrecsSerialization.PopulateBuiltInSerializers(registry);
            return registry;
        }
    }
}
