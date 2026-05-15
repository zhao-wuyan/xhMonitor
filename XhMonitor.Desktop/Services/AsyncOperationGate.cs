using System.Threading;

namespace XhMonitor.Desktop.Services;

internal sealed class AsyncOperationGate
{
    private int _isRunning;

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public bool TryEnter(out IDisposable scope)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            scope = NoopDisposable.Instance;
            return false;
        }

        scope = new ReleaseScope(this);
        return true;
    }

    private void Release()
    {
        Volatile.Write(ref _isRunning, 0);
    }

    private sealed class ReleaseScope : IDisposable
    {
        private AsyncOperationGate? _owner;

        public ReleaseScope(AsyncOperationGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
