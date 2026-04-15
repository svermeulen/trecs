using System;

namespace Trecs.Internal
{
    public static class JobsUtil
    {
        static readonly int ProcessorCount = Environment.ProcessorCount;

        /// <summary>
        /// Chooses a reasonable batch size to use based on the current
        /// environment
        /// </summary>
        public static int ChooseBatchSize(int totalIterations)
        {
            var iterationsPerBatch = totalIterations / ProcessorCount;

            if (iterationsPerBatch < 64)
            {
                return 64;
            }

            return iterationsPerBatch;
        }
    }
}
