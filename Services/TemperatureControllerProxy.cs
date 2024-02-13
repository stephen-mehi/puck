namespace Puck.Services;

public class TemperatureControllerProxy
{
    private readonly FujiPXFDriverProvider _prov;
    private FujiPXFDriver _proxy;
    private Func<Task<FujiPXFDriver>> _connectAction;
    private readonly SemaphoreSlim _lock;

    private readonly CancellationTokenSource _ctSrc;
    private readonly Task _task;
    private bool _isDisposed;

    private double? _processValue = null;
    private double? _setValue = null;


    public TemperatureControllerProxy(
        FujiPXFDriverProvider prov)
    {
        _prov = prov;
        _lock = new SemaphoreSlim(1, 1);
        _ctSrc = new CancellationTokenSource();

        var portConfig = new
            FujiPXFDriverPortConfiguration(
                "/dev/ttyUSB0",
                TimeSpan.FromSeconds(3));

        _connectAction = new Func<Task<FujiPXFDriver>>(async () =>
        {
            return await (new FujiPXFDriverProvider()).ConnectAsync(portConfig);
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
                    if (_proxy == null || !await _proxy.IsConnectedAsync(ct))
                    {
                        await _lock.WaitAsync(ct);

                        _processValue = null;
                        _setValue = null;

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

                    var procVal = await _proxy.GetProcessValueAsync(ct);
                    var setVal = await _proxy.GetSetValueAsync(ct);

                    await _lock.WaitAsync(ct);

                    _processValue = procVal;
                    _setValue = setVal;

                    _lock.Release();

                }
                catch (Exception)
                {

                }
                finally
                {
                    await Task.Delay(250, ct);
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

    public double? GetSetValue() => _setValue;
    public double? GetProcessValue() => _processValue;


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