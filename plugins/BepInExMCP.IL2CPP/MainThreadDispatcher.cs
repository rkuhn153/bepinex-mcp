using System.Collections.Concurrent;
using UnityEngine;

namespace BepInExMCP.IL2CPP;

internal sealed class MainThreadDispatcher : MonoBehaviour
{
    public MainThreadDispatcher(IntPtr pointer) : base(pointer)
    {
    }

    public void Update()
    {
        MainThreadQueue.Drain();
        MainThreadQueue.Tick();
    }

    public void OnDestroy()
    {
        MainThreadQueue.Stop(new ObjectDisposedException(nameof(MainThreadDispatcher)));
    }
}

internal static class MainThreadQueue
{
    private const int MaxJobsPerFrame = 256;
    private static readonly ConcurrentQueue<WorkItem> Jobs = new();
    private static volatile bool acceptingJobs;
    private static Action? frameTick;

    internal static void Start()
    {
        acceptingJobs = true;
    }

    internal static void SetFrameTick(Action? callback)
    {
        frameTick = callback;
    }

    internal static Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!acceptingJobs)
        {
            throw new InvalidOperationException("The Unity main-thread dispatcher is not running.");
        }

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(() => action(), completion, cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        Jobs.Enqueue(item);
        return AwaitTyped<T>(completion.Task);
    }

    internal static void Drain()
    {
        for (var i = 0; i < MaxJobsPerFrame && Jobs.TryDequeue(out var job); i++)
        {
            job.Execute();
        }
    }

    internal static void Stop(Exception reason)
    {
        acceptingJobs = false;
        frameTick = null;

        while (Jobs.TryDequeue(out var job))
        {
            job.Fail(reason);
        }
    }

    internal static void Tick()
    {
        frameTick?.Invoke();
    }

    private static async Task<T> AwaitTyped<T>(Task<object?> task)
    {
        var value = await task.ConfigureAwait(false);
        return value is null ? default! : (T)value;
    }

    private sealed class WorkItem
    {
        private readonly Func<object?> action;
        private readonly TaskCompletionSource<object?> completion;
        private readonly CancellationToken cancellationToken;

        internal WorkItem(
            Func<object?> action,
            TaskCompletionSource<object?> completion,
            CancellationToken cancellationToken)
        {
            this.action = action;
            this.completion = completion;
            this.cancellationToken = cancellationToken;
        }

        internal void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        internal void Fail(Exception reason)
        {
            completion.TrySetException(reason);
        }
    }
}
