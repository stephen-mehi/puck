
using puck.Services;

namespace Puck.Services;

public class SystemService : IHostedService, IDisposable
{
    private bool _isDisposed;
    private Task? _workTask;
    private readonly ILogger<SystemService> _logger;
    private readonly SystemProxy _proxy;
    private readonly CancellationTokenSource _ctSrc;

    public SystemService(
        ILogger<SystemService> logger,
        SystemProxy proxy)
    {
        _proxy = proxy;
        _logger = logger;
        _ctSrc = new CancellationTokenSource();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Background service is starting.");

        if (_workTask == null)
        {
            bool setAllIdle = false;

            while (!setAllIdle)
            {
                try
                {
                    //await _proxy.SetAllIdleInternalAsync(ct);
                    setAllIdle = true;
                }
                catch(Exception e)
                {
                    _logger.LogError(e, "FAILED TO SET SYSTEM TO IDLE IN STARTED");
                    await Task.Delay(2000, ct);
                }
            }

            _workTask = _proxy.StartRunScan(_ctSrc.Token);
        }

        await Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("BACKGROUND SERVICE IS STOPPING");
        _logger.LogInformation("CANCELLING SYSTEM WORK");
        try
        {
            _ctSrc.Cancel();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "ERROR OCCURRED WHILE CANCELLING SYSTEM WORK");
        }

        _logger.LogInformation("WAITING FOR SCAN TASK TO STOP");

        try
        {
            _workTask?.Wait(3000);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "ERROR OCCURRED WHILE WAITING FOR WORK TASK TO COMPLETE");
        }

        _logger.LogInformation("SETTING SYSTEM IDLE");

        try
        {
            _proxy.SetAllIdleAsync(CancellationToken.None).Wait(1000);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "ERROR OCCURRED WHILE SETTING SYSTEM IDLE");
        }

        _logger.LogInformation("DISPOSING SYSTEM PROXY");

        try
        {
            _proxy?.Dispose();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "ERROR OCCURRED WHILE DISPOSING SYSTEM PROXY");
        }

        try
        {
            _ctSrc?.Dispose();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "ERROR OCCURRED WHILE DISPOSING CANCELLATION TOKEN");
        }

        _logger.LogInformation("DONE STOPPING SYSTEM PROXY SERVICE");

        return Task.CompletedTask;
    }

    #region IDisposable

    protected void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        if (disposing)
        {
            if (!_ctSrc.IsCancellationRequested)
            {
                _logger.LogInformation("Setting system idle");

                _ctSrc.Cancel();
                _workTask?.Wait(5000);
                _proxy.SetAllIdleAsync(CancellationToken.None).Wait(5000);

                _logger.LogInformation("Disposing system proxy");
                _proxy.Dispose();
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
