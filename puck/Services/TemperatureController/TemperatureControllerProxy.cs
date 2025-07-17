using Puck.Services;
using Puck.Services.TemperatureController;
using System.Threading.Tasks;

namespace puck.Services.TemperatureController;

public class TemperatureControllerProxy : ITemperatureController
{
    private readonly FujiPXFDriverProvider _prov;
    private FujiPXFDriver? _proxy;
    private Func<Task<FujiPXFDriver>> _connectAction;
    private readonly SemaphoreSlim _lock;

    private readonly CancellationTokenSource _ctSrc;
    private readonly Task _task;
    private bool _isDisposed;
    private readonly ILogger<TemperatureControllerProxy> _logger;

    private readonly PauseContainer _pauseCont;


    public TemperatureControllerProxy(
        FujiPXFDriverProvider prov,
        ILogger<TemperatureControllerProxy> logger,
        PauseContainer pauseCont,
        string port)
    {
        _pauseCont = pauseCont;
        _prov = prov;
        _lock = new SemaphoreSlim(1, 1);
        _ctSrc = new CancellationTokenSource();

        _logger = logger;

        //"/dev/ttyUSB0"

        var portConfig = new
            FujiPXFDriverPortConfiguration(
                port,
                TimeSpan.FromSeconds(3));

        _connectAction = new Func<Task<FujiPXFDriver>>(async () =>
        {
            return await _prov.ConnectAsync(portConfig);
        });

        _task = StartReadLoop(_ctSrc.Token);
    }

    private Task StartReadLoop(CancellationToken ct)
    {
        var t = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _pauseCont.WaitIfPausedAsync(ct);

                    if (_proxy == null || !await _proxy.IsConnectedAsync(ct))
                    {
                        await _lock.WaitAsync(ct);

                        try
                        {
                            _proxy?.Dispose();
                            _proxy = await _connectAction();
                        }
                        finally
                        {
                            _lock.Release();
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, $"Error within {nameof(TemperatureControllerProxy)} in {nameof(StartReadLoop)}: {e.Message}");
                    await Task.Delay(1000, ct);
                }
                finally
                {
                    await Task.Delay(100, ct);
                }
            }
        });

        return t;
    }

    public async Task SetSetPointAsync(
        int setpoint,
        CancellationToken ct)
    {
        if (_proxy == null)
            throw new Exception("No connection to temperature controller");

        await _lock.WaitAsync(ct);

        try
        {
            await _proxy.SetSetValueAsync(setpoint, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DisableControlLoopAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);

        try
        {
            if (_proxy == null)
                throw new Exception("System proxy was null");

            await _proxy.DisableControlLoopAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task EnableControlLoopAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);

        try
        {
            if (_proxy == null)
                throw new Exception("System proxy was null");

            await _proxy.EnableControlLoopAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ApplySetPointSynchronouslyAsync(
            int tempSetPoint,
            double tolerance,
            TimeSpan timeout,
            CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        var startTime = DateTime.UtcNow;

        if (_proxy == null)
            throw new Exception("System proxy was null");

        try
        {

            await _proxy.SetSetValueAsync(tempSetPoint, ct);
            await _proxy.EnableControlLoopAsync(ct);

            while (true)
            {
                if (DateTime.UtcNow - startTime > timeout)
                    throw new TimeoutException($"Failed to get to specified temperature: {tempSetPoint} after specified seconds: {timeout.TotalSeconds}");

                if (Math.Abs(tempSetPoint - (await GetProcessValueAsync(ct))) < tolerance)
                    break;

                await Task.Delay(100, ct);
            }
        }
        catch (Exception)
        {
            await _proxy.DisableControlLoopAsync(ct);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<double> GetSetValueAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);

        try
        {
            if (_proxy == null)
                throw new Exception("System proxy was null");

            return await _proxy.GetSetValueAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }
    public async Task<double> GetProcessValueAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);

        try
        {
            if (_proxy == null)
                throw new Exception("System proxy was null");

            return await _proxy.GetProcessValueAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }


    #region IDisposable

    protected void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        if (disposing)
        {
            _ctSrc.Cancel();
            _task.Wait(3000);
            _proxy?.Dispose();
            _lock.Dispose();
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