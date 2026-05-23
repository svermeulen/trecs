using Unity.Jobs.LowLevel.Unsafe;

namespace Trecs.Internal
{
    public static class JobsUtil
    {
        // ~4 batches per worker gives Unity's work-stealing scheduler enough
        // headroom to redistribute when one batch runs longer than average
        // (cache misses, OS jitter, per-entity branchiness) without letting
        // per-batch dispatch overhead dominate. Tune by measurement.
        const int TargetBatchesPerWorker = 4;

        // Floor on batch size — amortizes per-batch dispatch overhead. Too
        // small and tiny jobs burn cycles on bookkeeping; too large and we
        // lose the work-stealing benefit of having multiple batches per
        // worker. 32 lands in the sweet spot for typical Trecs auto-job
        // bodies; tune if you have a workload that benefits from a different
        // value.
        const int MinBatchSize = 32;

        /// <summary>
        /// Chooses a reasonable batch size for an <c>IJobParallelForBatch</c>
        /// schedule.
        /// <para>
        /// Aims to produce roughly <see cref="TargetBatchesPerWorker"/> times
        /// the Unity job-worker count batches, floored at
        /// <see cref="MinBatchSize"/>. Uses
        /// <c>JobsUtility.JobWorkerCount</c> — Unity's actual worker pool —
        /// rather than <c>Environment.ProcessorCount</c>, which over-counts
        /// on systems with hyperthreading or efficiency/performance core mixes.
        /// </para>
        /// </summary>
        public static int ChooseBatchSize(int totalIterations)
        {
            // JobWorkerCount can be 0 if the user has disabled job-system
            // parallelism; clamp to 1 so we still produce a valid batch size.
            var workerCount = JobsUtility.JobWorkerCount;
            if (workerCount < 1)
            {
                workerCount = 1;
            }

            var batchSize = totalIterations / (workerCount * TargetBatchesPerWorker);
            return batchSize < MinBatchSize ? MinBatchSize : batchSize;
        }
    }
}
