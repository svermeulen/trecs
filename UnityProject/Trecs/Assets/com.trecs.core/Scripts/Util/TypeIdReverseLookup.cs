using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Trecs.Collections;
using Debug = UnityEngine.Debug;

namespace Trecs.Internal
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class TypeIdReverseLookup
    {
        static readonly IterableDictionary<TypeId, Type> _idToType = new();
        static readonly Dictionary<Type, TypeId> _typeToId = new();

        public static void Register(Type type, TypeId id)
        {
            if (!UnityThreadHelper.IsMainThread)
            {
                var t = Thread.CurrentThread;
                Debug.LogError(
                    $"TypeIdReverseLookup.Register off main thread "
                        + $"for {type?.FullName ?? "<null>"} (id={id.Value}). "
                        + $"Thread id={t.ManagedThreadId} "
                        + $"name='{t.Name ?? "<unnamed>"}' "
                        + $"isThreadPool={t.IsThreadPoolThread} "
                        + $"isBackground={t.IsBackground}\n"
                        + new StackTrace(fNeedFileInfo: true)
                );
            }
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);

            if (_typeToId.TryGetValue(type, out var existingId))
            {
                TrecsDebugAssert.That(
                    existingId == id,
                    "TypeId remapping: {0} was registered with ID {1} but is now being registered with ID {2}.",
                    type.FullName,
                    existingId,
                    id
                );
                return;
            }

            if (_idToType.TryGetValue(id, out var existingType))
            {
                // Two distinct cases reach here:
                //  - existingType != type: legitimate user-error collision (two types hash to
                //    the same id). Surface with the actionable [TypeId] message.
                //  - existingType == type: would mean _idToType has (id, type) but _typeToId
                //    is missing (type, id). The two maps are always updated together below,
                //    so this is an invariant violation.
                TrecsDebugAssert.That(
                    existingType != type,
                    "_typeToId/_idToType out of sync: id {0} maps to {1} in _idToType but {1} is missing from _typeToId.",
                    id,
                    type.FullName
                );
                TrecsDebugAssert.That(
                    false,
                    "TypeId collision: {0} and {1} both resolve to ID {2}",
                    type.FullName,
                    existingType.FullName,
                    id
                );
            }

            _typeToId.Add(type, id);
            _idToType.Add(id, type);
        }

        public static bool IsRegistered(Type type)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            return _typeToId.ContainsKey(type);
        }

        public static Type GetTypeFromId(TypeId id)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            if (_idToType.TryGetValue(id, out var type))
            {
                return type;
            }

            throw TrecsDebugAssert.CreateException("Unrecognized type ID {0}", id);
        }

        /// <summary>
        /// Non-throwing variant of <see cref="GetTypeFromId"/>. Used by diagnostic paths
        /// (e.g. leak-warning name rendering) that need to gracefully degrade when an
        /// ID hasn't been registered — typically because the value originated outside
        /// any <c>TypeId&lt;T&gt;</c> path (raw chunk-store allocation, snapshot
        /// restore prior to managed-side warmup).
        /// </summary>
        public static bool TryGetTypeFromId(TypeId id, out Type type)
        {
            TrecsDebugAssert.That(UnityThreadHelper.IsMainThread);
            return _idToType.TryGetValue(id, out type);
        }
    }
}
