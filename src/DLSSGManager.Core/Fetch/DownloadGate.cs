namespace DLSSGManager;

public static partial class ModFetcher
{
    /// <summary>
    /// Pause and skip switches for a running download.
    ///
    /// Pausing stops reading inside the transfer loop. The connection is left open; if the server
    /// drops it during a long pause, the transfer continues with a ranged request from the byte it
    /// reached. Skipping abandons only the source in use, so automatic mode moves on to the next one.
    /// Cancellation stays with the caller's token and is immediate.
    /// </summary>
    public sealed class DownloadGate
    {
        private readonly object _sync = new();
        private TaskCompletionSource _resumed = Released();
        private CancellationTokenSource? _attempt;
        private bool _paused;

        public bool Paused
        {
            get { lock (_sync) return _paused; }
        }

        public void Pause()
        {
            lock (_sync)
            {
                if (_paused) return;
                _paused = true;
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            TaskCompletionSource released;
            lock (_sync)
            {
                if (!_paused) return;
                _paused = false;
                released = _resumed;
            }
            released.TrySetResult();
        }

        /// <summary>Gives up on the source being used. Also releases a pause, so the next source starts at once.</summary>
        public void SkipCurrentSource()
        {
            CancellationTokenSource? attempt;
            lock (_sync) attempt = _attempt;
            try { attempt?.Cancel(); }
            catch (ObjectDisposedException) { /* the attempt finished in the meantime */ }
            Resume();
        }

        /// <summary>Completes at once while running; waits while paused.</summary>
        public Task WaitAsync(CancellationToken ct)
        {
            Task resumed;
            lock (_sync) resumed = _resumed.Task;
            return resumed.IsCompleted ? Task.CompletedTask : resumed.WaitAsync(ct);
        }

        /// <summary>A token for one source attempt: cancelled by the caller's token or by <see cref="SkipCurrentSource"/>.</summary>
        internal CancellationToken BeginAttempt(CancellationToken outer)
        {
            lock (_sync)
            {
                _attempt?.Dispose();
                _attempt = CancellationTokenSource.CreateLinkedTokenSource(outer);
                return _attempt.Token;
            }
        }

        internal void EndAttempt()
        {
            lock (_sync)
            {
                _attempt?.Dispose();
                _attempt = null;
            }
        }

        private static TaskCompletionSource Released()
        {
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            released.SetResult();
            return released;
        }
    }
}
