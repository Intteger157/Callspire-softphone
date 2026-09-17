using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone.Service
{
    /// <summary>
    /// Single-threaded work queue that plays the role of the "UI thread" for the headless sidecar.
    /// <see cref="DesktopAppController"/> and the ViewModels marshal every state mutation through
    /// <see cref="UiThread"/>; routing those posts here keeps the ViewModels single-threaded exactly as
    /// they are under WPF/Avalonia, so state projection to Swift observes consistent snapshots.
    /// </summary>
    public sealed class ServiceDispatcher : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        private int _threadId;

        public ServiceDispatcher()
        {
            _thread = new Thread(Run) { Name = "Callspire.Service dispatcher", IsBackground = true };
            _thread.Start();
        }

        public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

        public void Post(Action action)
        {
            if (_queue.IsAddingCompleted) return;
            try { _queue.Add(action); } catch (InvalidOperationException) { }
        }

        public Task InvokeAsync(Action action)
        {
            if (CheckAccess()) { action(); return Task.CompletedTask; }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try { action(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            if (CheckAccess()) return Task.FromResult(func());
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        /// <summary>
        /// Runs an async function on the dispatcher thread and unwraps its task. Named differently from
        /// <see cref="InvokeAsync{T}(Func{T})"/> because a lambda ending in <c>return null;</c> would otherwise
        /// bind to the <c>Func&lt;Task&lt;T&gt;&gt;</c> overload and be awaited as a null task.
        /// </summary>
        public Task<T> RunAsync<T>(Func<Task<T>> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async () =>
            {
                try { tcs.TrySetResult(await func().ConfigureAwait(false)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        public Task RunAsync(Func<Task> func)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async () =>
            {
                try { await func().ConfigureAwait(false); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        private void Run()
        {
            _threadId = Environment.CurrentManagedThreadId;
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { AppLog.Log($"[Dispatcher] unhandled: {ex}"); }
            }
        }

        public void Dispose()
        {
            try { _queue.CompleteAdding(); } catch { }
        }
    }
}
