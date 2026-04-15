using UnityEngine;

namespace Trecs.Internal
{
    public static class UnityThreadUtil
    {
        static UnityThreadUtil()
        {
            MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void OnStart()
        {
            Assert.That(MainThreadId == System.Threading.Thread.CurrentThread.ManagedThreadId);
        }

        public static bool IsMainThread =>
            System.Threading.Thread.CurrentThread.ManagedThreadId == MainThreadId;

        public static int MainThreadId { get; }
    }
}
