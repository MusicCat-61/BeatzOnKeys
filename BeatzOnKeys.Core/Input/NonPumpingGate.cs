namespace BeatzOnKeys.Core.Input;

/// <summary>
/// A mutual-exclusion gate that never asks the thread's synchronization context to wait.
/// </summary>
/// <remarks>
/// <c>lock</c> takes the slow path as soon as the gate is contended, and on a thread that has a
/// <see cref="System.Threading.SynchronizationContext"/> which requires wait notification — a WPF
/// dispatcher, for instance — that slow path calls <c>SynchronizationContext.Wait</c>, which pumps
/// the message queue while the caller is still inside its critical section. Pumping there lets
/// unrelated dispatcher work run against a half-finished install, which is what
/// <c>KeyboardHook.NativeLifecycleDoesNotNeedCallerPump</c> forbids and what the CI runner caught:
/// the gate was uncontended on a quiet desktop and contended on a loaded runner.
///
/// <see cref="Monitor.TryEnter(object)"/> is not a way out. It also ends up in
/// <c>Monitor.Enter_Slowpath</c> under contention and pumps for the same reason — measured rather
/// than assumed: the first version of this type used it, and the contention test failed with the
/// same stack. So the gate is a spin lock built on <see cref="Interlocked"/> alone, which never
/// consults the synchronization context. The critical sections here are a handful of field
/// assignments, so a short spin costs less than any wait would.
///
/// Re-entry is reported instead of tolerated. A plain spin lock would hang, and a hang in the
/// input path is the worst failure mode available; no current caller nests, which the suite
/// exercises end to end, so this throw is a tripwire for a future one rather than a path.
/// </remarks>
internal sealed class NonPumpingGate
{
    private int _flag;
    private int _owner;

    /// <summary>
    /// Acquires the gate without blocking on a kernel wait or touching the synchronization
    /// context. Dispose the returned scope to release it.
    /// </summary>
    public Scope Enter()
    {
        var current = Environment.CurrentManagedThreadId;
        var spin = new SpinWait();
        while (true)
        {
            if (Volatile.Read(ref _owner) == current && Volatile.Read(ref _flag) != 0)
            {
                throw new InvalidOperationException(
                    "The non-pumping gate was re-entered on the thread that already holds it. " +
                    "Acquire it once and pass what the inner call needs.");
            }

            if (Interlocked.CompareExchange(ref _flag, 1, 0) == 0)
            {
                Volatile.Write(ref _owner, current);
                return new Scope(this);
            }

            // Yield rather than wait: no synchronization context is consulted, so no message
            // queue is pumped while the other owner is still working.
            spin.SpinOnce();
        }
    }

    private void Exit()
    {
        Volatile.Write(ref _owner, 0);
        Volatile.Write(ref _flag, 0);
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly NonPumpingGate _gate;

        internal Scope(NonPumpingGate gate) => _gate = gate;

        public void Dispose() => _gate.Exit();
    }
}
