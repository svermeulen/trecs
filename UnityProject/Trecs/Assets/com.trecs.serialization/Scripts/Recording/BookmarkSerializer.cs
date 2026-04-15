using System;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class BookmarkSerializer : IDisposable
    {
        static readonly TrecsLog _log = new(nameof(BookmarkSerializer));

        readonly IGameStateSerializer _gameStateSerializer;
        readonly SerializationBuffer _serializerHelper;
        readonly BlobCache _blobCache;
        readonly WorldAccessor _world;

        public BookmarkSerializer(
            IGameStateSerializer gameStateSerializer,
            BlobCache blobCache,
            World ecsProvider,
            SerializerRegistry serializerManager
        )
        {
            _gameStateSerializer = gameStateSerializer;
            _blobCache = blobCache;
            _world = ecsProvider.CreateAccessor();
            _serializerHelper = new SerializationBuffer(serializerManager);
        }

        public SerializationBuffer SerializerHelper
        {
            get { return _serializerHelper; }
        }

        /// <summary>
        /// Save a bookmark of the current game state to the internal SerializerHelper.
        /// After calling this, read from SerializerHelper.MemoryStream to persist to disk.
        /// </summary>
        public void Save(int version, bool includeTypeChecks, int numConnections = 0)
        {
            _gameStateSerializer.StartSerialize(
                version,
                _serializerHelper,
                _gameStateSerializer.SerializationFlags,
                includeTypeChecks: includeTypeChecks
            );

            var metadata = new BookmarkMetadata()
            {
                NumConnections = numConnections,
                FixedFrame = _world.FixedFrame,
            };

            _blobCache.GetAllActiveBlobIds(metadata.BlobIds);

            _serializerHelper.Write("metadata", metadata);
            _gameStateSerializer.SerializeCurrentState(_serializerHelper);
            var numBytes = _serializerHelper.EndWrite();

            _log.Trace("Saved bookmark ({0.00} kb)", numBytes / 1024f);
        }

        /// <summary>
        /// Load a bookmark from a pre-loaded SerializationBuffer.
        /// The caller is responsible for loading the bookmark data into the
        /// serializerHelper's memory stream before calling this.
        /// Returns false if the static seed is incompatible.
        /// </summary>
        public bool Load(SerializationBuffer serializerHelper)
        {
            if (
                !_gameStateSerializer.StartDeserialize(
                    serializerHelper,
                    _gameStateSerializer.SerializationFlags
                )
            )
            {
                return false;
            }

            var _ = serializerHelper.Read<BookmarkMetadata>("metadata");
            _gameStateSerializer.DeserializeCurrentState(serializerHelper);
            serializerHelper.StopRead(verifySentinel: true);

            return true;
        }

        /// <summary>
        /// Read just the metadata from a pre-loaded bookmark without deserializing the full state.
        /// The caller is responsible for loading the bookmark data into the
        /// serializerHelper's memory stream before calling this.
        /// </summary>
        public BookmarkMetadata PeekForMetadata(SerializationBuffer serializerHelper)
        {
            var succeeded = _gameStateSerializer.StartDeserialize(
                serializerHelper,
                _gameStateSerializer.SerializationFlags
            );
            Assert.That(succeeded);

            var metadata = serializerHelper.Read<BookmarkMetadata>("metadata");

            serializerHelper.StopRead(verifySentinel: false);

            return metadata;
        }

        public void Dispose()
        {
            _serializerHelper.Dispose();
        }
    }
}
