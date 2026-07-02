using System;

namespace Filey
{
    public static class MemoryManager
    {
        private static readonly object _gcLock = new object();

        public static void ReleaseUnusedMemory()
        {
            lock (_gcLock)
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
                GC.WaitForPendingFinalizers();
            }
        }
    }
}
