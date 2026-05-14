using System.Threading;
using UnityEngine;

namespace Trecs.Internal
{
    public static class UnityThreadHelper
    {
        static UnityThreadHelper()
        {
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void OnStart()
        {
            TrecsAssert.That(MainThreadId == Thread.CurrentThread.ManagedThreadId);
        }

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        public static int MainThreadId { get; }
    }
}
