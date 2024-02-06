
namespace Puck.Services;

public class SystemService : IHostedService, IDisposable
{
    private bool _isDisposed;
    private readonly ILogger<SystemService> _logger;
    private Task? _workTask;
    private readonly PhoenixProxy _proxy;
    private readonly CancellationTokenSource _ctSrc;

    public SystemService(
        ILogger<SystemService> logger,
        PhoenixProxy proxy)
    {
        _proxy = proxy;
        _logger = logger;
        _ctSrc = new CancellationTokenSource();
    }

    public Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Background service is starting.");

        if (_workTask == null)
        {
            _workTask =
                Task.Run(() =>
                {
                    //ESPRESSO CONTROL LOGIC SCAN HERE
                    while (!_ctSrc.IsCancellationRequested)
                    {
                        Task.Delay(500, _ctSrc.Token);
                    }
                }, _ctSrc.Token);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Background service is stopping.");

        _ctSrc.Cancel();

        return Task.CompletedTask;
    }

    #region IDisposable

    protected void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        if (disposing)
        {
            if(!_ctSrc.IsCancellationRequested)
            {
                _ctSrc.Cancel();
                _workTask?.Wait(3000);
                _ctSrc.Dispose();
            }
        }

        _isDisposed = true;
    }

    public void Dispose()
    {
        // Dispose of unmanaged resources.
        Dispose(true);
        // Suppress finalization.
        GC.SuppressFinalize(this);
    }

    #endregion
}