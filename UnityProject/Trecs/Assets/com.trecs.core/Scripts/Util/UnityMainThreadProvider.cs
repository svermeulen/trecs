using System.Threading;
using UnityEngine;

namespace Trecs.Internal
{
    public static class UnityThreadUtil
    {
        static UnityThreadUtil()
        {
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void OnStart()
        {
            Assert.That(MainThreadId == Thread.CurrentThread.ManagedThreadId);
        }

        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        public static int MainThreadId { get; }
    }
}
