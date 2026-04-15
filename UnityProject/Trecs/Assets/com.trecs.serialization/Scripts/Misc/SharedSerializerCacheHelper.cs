using System;
using Trecs.Internal;

namespace Trecs.Serialization
{
    public class SharedSerializerCacheHelper : IDisposable
    {
        readonly SerializerRegistry _registry;

        SerializationBuffer _buffers;
        bool _isInUse;

        public SharedSerializerCacheHelper(SerializerRegistry registry)
        {
            _registry = registry;
        }

        public IDisposable Borrow(out SerializationBuffer buffer)
        {
            Assert.That(
                !_isInUse,
                "SharedSerializerCacheHelper is already in use. Nested usage is not supported."
            );
            _isInUse = true;

            if (_buffers == null)
            {
                _buffers = new(_registry);
            }

            buffer = _buffers;
            return new BorrowedBuffer(this);
        }

        public void Dispose()
        {
            _buffers?.Dispose();
        }

        readonly struct BorrowedBuffer : IDisposable
        {
            readonly SharedSerializerCacheHelper _owner;

            public BorrowedBuffer(SharedSerializerCacheHelper owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                Assert.That(_owner._isInUse);
                _owner._isInUse = false;
            }
        }
    }
}
