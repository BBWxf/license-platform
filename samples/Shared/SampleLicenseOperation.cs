using System;
using System.Threading;
using System.Threading.Tasks;
using Licensing.Client;

namespace Licensing.Samples
{
    internal sealed class SampleLicenseOperation
    {
        private readonly object _gate = new object();
        private Task<LicenseResult>? _runningTask;
        private RunCancellation? _runningCancellation;
        private Task? _closeTask;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                    return _runningTask != null && !_runningTask.IsCompleted;
            }
        }

        public bool IsClosing
        {
            get
            {
                lock (_gate)
                    return _closeTask != null;
            }
        }

        public Task<LicenseResult> RunAsync(LicenseClient client, string? licenseKey)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return RunAsync((key, token) => key == null
                ? client.StartAsync(token)
                : client.StartAsync(key, token), licenseKey);
        }

        internal Task<LicenseResult> RunAsync(
            Func<string?, CancellationToken, Task<LicenseResult>> operation,
            string? licenseKey)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            var key = string.IsNullOrWhiteSpace(licenseKey) ? null : licenseKey;
            lock (_gate)
            {
                if (_closeTask != null)
                    throw new InvalidOperationException("The sample is closing.");
                if (_runningTask != null && !_runningTask.IsCompleted)
                    throw new InvalidOperationException("A licensing operation is already running.");

                var cancellation = new RunCancellation();
                var token = cancellation.Token;
                _runningCancellation = cancellation;
                // Even an async SDK method can block before its first yield (for example, DPAPI locking).
                // Publish the single tracked task under the gate; never invoke the SDK on the caller first.
                _runningTask = Task.Run(async () =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        return await operation(key, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return LicenseResult.Failed(new LicenseError(
                            LicenseErrorCode.OperationCancelled, "The licensing operation was cancelled."));
                    }
                    catch (Exception)
                    {
                        return LicenseResult.Failed(new LicenseError(
                            LicenseErrorCode.ClientFailure, "The licensing operation failed unexpectedly."));
                    }
                    finally
                    {
                        cancellation.Dispose();
                    }
                });
                return _runningTask;
            }
        }

        public void Cancel()
        {
            RunCancellation? cancellation;
            lock (_gate)
                cancellation = _runningTask != null && !_runningTask.IsCompleted ? _runningCancellation : null;
            cancellation?.Cancel();
        }

        public Task CloseAsync(Action disposeResources)
        {
            if (disposeResources == null) throw new ArgumentNullException(nameof(disposeResources));
            Task<LicenseResult>? runningTask;
            RunCancellation? cancellation;
            TaskCompletionSource<bool> completion;
            lock (_gate)
            {
                if (_closeTask != null)
                    return _closeTask;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Publish before cancellation or cleanup: either can synchronously reenter the helper.
                _closeTask = completion.Task;
                runningTask = _runningTask;
                cancellation = _runningCancellation;
            }
            _ = CompleteCloseAsync(runningTask, cancellation, disposeResources, completion);
            return completion.Task;
        }

        private static async Task CompleteCloseAsync(
            Task<LicenseResult>? runningTask,
            RunCancellation? cancellation,
            Action disposeResources,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                cancellation?.Cancel();
                if (runningTask != null)
                    await runningTask.ConfigureAwait(false);
                // The first caller owns cleanup. It must not require a UI synchronization context.
                disposeResources();
                completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }

        private sealed class RunCancellation : IDisposable
        {
            private readonly object _gate = new object();
            private readonly CancellationTokenSource _source = new CancellationTokenSource();
            private bool _disposed;

            internal RunCancellation()
            {
                Token = _source.Token;
            }

            internal CancellationToken Token { get; }

            internal void Cancel()
            {
                // Never hold the helper state gate during callbacks; serialize cancellation with disposal.
                lock (_gate)
                {
                    if (_disposed) return;
                    try
                    {
                        _source.Cancel();
                    }
                    catch (AggregateException)
                    {
                        // A failing cancellation callback must not skip waiting for the operation or cleanup.
                    }
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _source.Dispose();
                }
            }
        }
    }
}
