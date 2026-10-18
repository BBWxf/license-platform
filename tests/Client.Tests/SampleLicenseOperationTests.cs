using System;
using System.Threading;
using System.Threading.Tasks;
using Licensing.Client;
using Licensing.Samples;
using Xunit;

namespace Client.Tests;

public sealed class SampleLicenseOperationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RunAsync_OffloadsBlockingPrefixAndLeavesCallerContextResponsive()
    {
        var helper = new SampleLicenseOperation();
        using var operation = new BlockingOperation();
        var context = new RecordingSynchronizationContext();
        var returned = NewCompletion<Task<LicenseResult>>();
        var callerExited = NewCompletion<bool>();
        var callerThreadId = 0;
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            callerThreadId = Environment.CurrentManagedThreadId;
            try
            {
                var task = helper.RunAsync(operation.Invoke, "sample-key");
                Assert.Same(context, SynchronizationContext.Current);
                returned.TrySetResult(task);
            }
            catch (Exception error)
            {
                returned.TrySetException(error);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                callerExited.TrySetResult(true);
            }
        })
        { IsBackground = true };

        try
        {
            caller.Start();
            var task = await returned.Task.WaitAsync(Timeout);
            await callerExited.Task.WaitAsync(Timeout);
            await operation.Entered.Task.WaitAsync(Timeout);

            Assert.True(helper.IsRunning);
            Assert.False(task.IsCompleted);
            Assert.NotEqual(callerThreadId, operation.ThreadId);
            Assert.Null(operation.Context);
            Assert.Equal(0, context.PostCount);

            operation.Release();
            Assert.Same(operation.Result, await task.WaitAsync(Timeout));
            Assert.False(helper.IsRunning);
        }
        finally
        {
            operation.Release();
            await callerExited.Task.WaitAsync(Timeout);
            await helper.CloseAsync(() => { }).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsRepeatedInvocationButAllowsAnotherCompletedRun()
    {
        var helper = new SampleLicenseOperation();
        using var operation = new BlockingOperation();
        var rejectedCalls = 0;
        try
        {
            var first = helper.RunAsync(operation.Invoke, null);
            Assert.True(helper.IsRunning);
            AssertSynchronousRejection(() =>
            {
                helper.RunAsync((_, _) =>
                {
                    Interlocked.Increment(ref rejectedCalls);
                    return Task.FromResult(operation.Result);
                }, null);
            });
            Assert.Equal(0, rejectedCalls);

            operation.Release();
            Assert.Same(operation.Result, await first.WaitAsync(Timeout));
            Assert.False(helper.IsRunning);
            var next = helper.RunAsync((_, _) => Task.FromResult(operation.Result), null);
            Assert.Same(operation.Result, await next.WaitAsync(Timeout));
        }
        finally
        {
            operation.Release();
            await helper.CloseAsync(() => { }).WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" \t\r\n", null)]
    [InlineData("sample-key", "sample-key")]
    [InlineData(" sample-key ", " sample-key ")]
    public async Task RunAsync_NormalizesOnlyMissingOrWhitespaceKeys(string? entered, string? expected)
    {
        var helper = new SampleLicenseOperation();
        string? observed = "not-invoked";
        try
        {
            await helper.RunAsync((key, _) =>
            {
                observed = key;
                return Task.FromResult(TestResult());
            }, entered).WaitAsync(Timeout);
            Assert.Equal(expected, observed);
        }
        finally
        {
            await helper.CloseAsync(() => { }).WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_MapsAlreadyCancelledTasksAndSynchronousCancellation(bool synchronous)
    {
        var helper = new SampleLicenseOperation();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            var result = await helper.RunAsync((_, _) => synchronous
                ? throw new OperationCanceledException("private cancellation detail", cancelled.Token)
                : Task.FromCanceled<LicenseResult>(cancelled.Token), null).WaitAsync(Timeout);

            Assert.False(result.Success);
            Assert.Equal(LicenseErrorCode.OperationCancelled, result.Error!.Code);
            Assert.Equal("The licensing operation was cancelled.", result.Error.Message);
            Assert.False(helper.IsRunning);
        }
        finally
        {
            await helper.CloseAsync(() => { }).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Cancel_RequestsCancellationWithoutCompletingEarlyOrCancellingTheNextRun()
    {
        var helper = new SampleLicenseOperation();
        using var operation = new BlockingOperation();
        try
        {
            helper.Cancel(); // Idle cancellation must not poison the next run.
            var task = helper.RunAsync(operation.Invoke, null);
            await operation.Entered.Task.WaitAsync(Timeout);
            Assert.False(operation.Token.IsCancellationRequested);

            helper.Cancel();
            Assert.True(operation.Token.IsCancellationRequested);
            Assert.False(task.IsCompleted);
            Assert.True(helper.IsRunning);
            operation.Release();
            var result = await task.WaitAsync(Timeout);
            Assert.Equal(LicenseErrorCode.OperationCancelled, result.Error!.Code);

            helper.Cancel(); // Completion may already have disposed this run's source.
            var next = await helper.RunAsync((_, token) =>
            {
                Assert.False(token.IsCancellationRequested);
                return Task.FromResult(operation.Result);
            }, null).WaitAsync(Timeout);
            Assert.Same(operation.Result, next);
        }
        finally
        {
            operation.Release();
            await helper.CloseAsync(() => { }).WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_MapsSynchronousAndAsynchronousUnexpectedExceptionsWithoutDetails(bool synchronous)
    {
        var helper = new SampleLicenseOperation();
        var error = new InvalidOperationException("private sample-key exception detail");
        try
        {
            var result = await helper.RunAsync((_, _) => synchronous
                ? throw error
                : Task.FromException<LicenseResult>(error), null).WaitAsync(Timeout);

            Assert.False(result.Success);
            Assert.Equal(LicenseErrorCode.ClientFailure, result.Error!.Code);
            Assert.Equal("The licensing operation failed unexpectedly.", result.Error.Message);
            Assert.DoesNotContain("private", result.Error.ToString());
            Assert.False(helper.IsRunning);
        }
        finally
        {
            await helper.CloseAsync(() => { }).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task CloseAsync_CancelsButWaitsForEntireOperationBeforeExactlyOnceCleanup()
    {
        var helper = new SampleLicenseOperation();
        using var operation = new BlockingOperation();
        var cleanupCount = 0;
        var ignoredCleanupCount = 0;
        try
        {
            var task = helper.RunAsync(operation.Invoke, null);
            await operation.Entered.Task.WaitAsync(Timeout);
            var close = helper.CloseAsync(() =>
            {
                Assert.True(operation.HasExited);
                Assert.True(task.IsCompleted);
                Interlocked.Increment(ref cleanupCount);
            });
            Assert.True(helper.IsClosing);
            Assert.True(operation.Token.IsCancellationRequested);
            Assert.False(task.IsCompleted);
            Assert.False(close.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref cleanupCount));
            Assert.Same(close, helper.CloseAsync(() => Interlocked.Increment(ref ignoredCleanupCount)));
            AssertSynchronousRejection(() => { helper.RunAsync(operation.Invoke, null); });

            // Returning a still-pending task from the SDK invocation is not operation completion.
            operation.ReleasePrefix();
            await operation.PrefixReturned.Task.WaitAsync(Timeout);
            Assert.True(helper.IsRunning);
            Assert.False(task.IsCompleted);
            Assert.False(close.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref cleanupCount));

            operation.Release();
            Assert.Equal(LicenseErrorCode.OperationCancelled, (await task.WaitAsync(Timeout)).Error!.Code);
            await close.WaitAsync(Timeout);
            Assert.False(helper.IsRunning);
            Assert.True(helper.IsClosing);
            Assert.Equal(1, Volatile.Read(ref cleanupCount));
            Assert.Equal(0, Volatile.Read(ref ignoredCleanupCount));
            Assert.Same(close, helper.CloseAsync(() => Interlocked.Increment(ref ignoredCleanupCount)));
            helper.Cancel();
            AssertSynchronousRejection(() => { helper.RunAsync(operation.Invoke, null); });
        }
        finally
        {
            operation.Release();
            await helper.CloseAsync(() => { }).WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task CloseAsync_WhenIdleIsIdempotentAndCleanupCanReenterClose()
    {
        var helper = new SampleLicenseOperation();
        var cleanupCount = 0;
        Task? reentered = null;
        var close = helper.CloseAsync(() =>
        {
            Interlocked.Increment(ref cleanupCount);
            reentered = helper.CloseAsync(() => Interlocked.Increment(ref cleanupCount));
        });

        await close.WaitAsync(Timeout);
        Assert.Same(close, reentered);
        Assert.Same(close, helper.CloseAsync(() => Interlocked.Increment(ref cleanupCount)));
        Assert.Equal(1, cleanupCount);
        Assert.True(helper.IsClosing);
        Assert.False(helper.IsRunning);
        helper.Cancel();
        AssertSynchronousRejection(() =>
        {
            helper.RunAsync((_, _) => Task.FromResult(TestResult()), null);
        });
    }

    [Fact]
    public async Task CloseAsync_CleanupFailureIsNotRetried()
    {
        var helper = new SampleLicenseOperation();
        var cleanupCount = 0;
        var close = helper.CloseAsync(() =>
        {
            Interlocked.Increment(ref cleanupCount);
            throw new InvalidOperationException("Test cleanup failure.");
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => close.WaitAsync(Timeout));
        Assert.Same(close, helper.CloseAsync(() => Interlocked.Increment(ref cleanupCount)));
        Assert.Equal(1, cleanupCount);
        Assert.True(helper.IsClosing);
    }

    private static void AssertSynchronousRejection(Action invoke)
    {
        // Admission errors are synchronous even though an accepted call returns a Task.
        var error = Record.Exception(invoke);
        Assert.IsType<InvalidOperationException>(error);
    }

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

    private static LicenseResult TestResult() => LicenseResult.Failed(
        new LicenseError(LicenseErrorCode.ActivationRequired, "Test activation required."));

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;
        internal int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCount);
            base.Post(callback, state);
        }
    }

    private sealed class BlockingOperation : IDisposable
    {
        private readonly ManualResetEventSlim _prefixRelease = new ManualResetEventSlim(false);
        private readonly TaskCompletionSource<bool> _finish = NewCompletion<bool>();
        private int _exited;

        internal TaskCompletionSource<bool> Entered { get; } = NewCompletion<bool>();
        internal TaskCompletionSource<bool> PrefixReturned { get; } = NewCompletion<bool>();
        internal LicenseResult Result { get; } = TestResult();
        internal CancellationToken Token { get; private set; }
        internal int ThreadId { get; private set; }
        internal SynchronizationContext? Context { get; private set; }
        internal bool HasExited => Volatile.Read(ref _exited) != 0;

        internal Task<LicenseResult> Invoke(string? key, CancellationToken token)
        {
            Token = token;
            ThreadId = Environment.CurrentManagedThreadId;
            Context = SynchronizationContext.Current;
            Entered.TrySetResult(true);
            if (!_prefixRelease.Wait(Timeout))
            {
                Volatile.Write(ref _exited, 1);
                throw new TimeoutException("The test did not release the synchronous prefix.");
            }
            PrefixReturned.TrySetResult(true);
            return FinishAsync(token);
        }

        private async Task<LicenseResult> FinishAsync(CancellationToken token)
        {
            try
            {
                // Deliberately ignore cancellation until explicitly released to model SDK unwinding.
                await _finish.Task.WaitAsync(Timeout).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return Result;
            }
            finally
            {
                Volatile.Write(ref _exited, 1);
            }
        }

        internal void ReleasePrefix() => _prefixRelease.Set();

        internal void Release()
        {
            _prefixRelease.Set();
            _finish.TrySetResult(true);
        }

        public void Dispose() => _prefixRelease.Dispose();
    }
}
