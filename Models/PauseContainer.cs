public class PauseContainer
{
    private readonly SemaphoreSlim _pauseLock;
    private readonly SemaphoreSlim _pauseStateLock;

    public PauseContainer()
    {
        _pauseLock = new SemaphoreSlim(1, 1);
        _pauseStateLock = new SemaphoreSlim(1, 1);

    }

    public async Task PauseAsync(CancellationToken ct)
    {
        await _pauseStateLock.WaitAsync(ct);

        try
        {
            if (_pauseLock.CurrentCount == 1)
            {
                await _pauseLock.WaitAsync(ct);
            }
        }
        finally
        {
            _pauseStateLock.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        await _pauseStateLock.WaitAsync(ct);

        try
        {
            if (_pauseLock.CurrentCount == 0)
            {
                _pauseLock.Release();
            }
        }
        finally
        {
            _pauseStateLock.Release();
        }
    }

    public async Task WaitIfPausedAsync(CancellationToken ct)
    {
        await _pauseLock.WaitAsync(ct);
        _pauseLock.Release();
    }
}