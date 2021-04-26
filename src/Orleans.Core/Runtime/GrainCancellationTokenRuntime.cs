using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Internal;

namespace Orleans.Runtime
{
    internal class GrainCancellationTokenRuntime : IGrainCancellationTokenRuntime
    {
        private const int MaxNumCancelErrorTries = 30;
        private readonly TimeSpan _cancelCallMaxWaitTime = TimeSpan.FromSeconds(300);
        private readonly IBackoffProvider _cancelCallBackoffProvider = new FixedBackoff(TimeSpan.FromSeconds(1));
        private readonly Func<Exception, int, bool> _cancelCallRetryExceptionFilter =
            (exception, i) => exception is GrainExtensionNotInstalledException;

        public Task Cancel(Guid id, CancellationTokenSource tokenSource, ConcurrentDictionary<GrainId, GrainReference> grainReferences)
        {
            if (tokenSource.IsCancellationRequested)
            {
                // This token has already been canceled.
                return Task.CompletedTask;
            }

            // propagate the exception from the _cancellationTokenSource.Cancel back to the caller
            // but also cancel _targetGrainReferences.
            Task localTask = null;
            try
            {
                // Cancel the token now, preventing recursion.
                tokenSource.Cancel();
            }
            catch (Exception exception)
            {
                localTask = Task.FromException(exception);
            }

            List<Task> tasks = null;
            foreach (var reference in grainReferences)
            {
                if (tasks is null)
                {
                    tasks = new();
                    if (localTask != null) tasks.Add(localTask);
                }
                tasks.Add(CancelTokenWithRetries(id, grainReferences, reference.Key, reference.Value.AsReference<ICancellationSourcesExtension>()));
            }

            return tasks is null ? localTask ?? Task.CompletedTask : Task.WhenAll(tasks);
        }

         private async Task CancelTokenWithRetries(
             Guid id,
             ConcurrentDictionary<GrainId, GrainReference> grainReferences,
             GrainId key,
             ICancellationSourcesExtension tokenExtension)
        {
            await AsyncExecutorWithRetries.ExecuteWithRetries(
                i => tokenExtension.CancelRemoteToken(id),
                MaxNumCancelErrorTries,
                _cancelCallRetryExceptionFilter,
                _cancelCallMaxWaitTime,
                _cancelCallBackoffProvider);
            grainReferences.TryRemove(key, out _);
        }
    }
}
