using System.Reactive;

namespace Puck.Services;

public struct DigitalIoState
{
    public DigitalIoState(
        bool state,
        DateTime timestamp)
    {
        State = state;
        TimeStamp = timestamp;
    }

    public bool State { get; }
    public DateTime TimeStamp { get; }
}

public struct AnalogIoState
{
    public AnalogIoState(
        double state,
        DateTime timestamp)
    {
        State = state;
        TimeStamp = timestamp;
    }

    public double State { get; }
    public DateTime TimeStamp { get; }
}

public class PhoenixProxy : IDisposable
{

    private readonly ITcpIOBusConnectionFactory _connectFactory;
    private IDisposableIOBus? _phoenix;
    private readonly Task _connectionLoop;
    private readonly CancellationTokenSource _ctSrc;
    private bool _isDisposed;
    private SemaphoreSlim _deviceLock;
    private Func<Task<IDisposableIOBus>> _connectAction;
    private readonly IReadOnlyList<ushort> _digitalInputs;
    private readonly IReadOnlyList<ushort> _digitalOutputs;
    private readonly IReadOnlyList<ushort> _analogInputs;
    private readonly IReadOnlyList<ushort> _analogOutputs;

    public PhoenixProxy(
        ITcpIOBusConnectionFactory connectFactory)
    {
        _ctSrc = new CancellationTokenSource();

        _digitalInputs =
            Enumerable
            .Range(1, 8)
            .Select(x => (ushort)x)
            .ToList();

        _digitalOutputs =
            Enumerable
            .Range(1, 4)
            .Select(x => (ushort)x)
            .ToList();

        _analogInputs =
            Enumerable
            .Range(1, 4)
            .Select(x => (ushort)x)
            .ToList();

        _analogOutputs =
            Enumerable
            .Range(1, 2)
            .Select(x => (ushort)x)
            .ToList();

        _digitalInputState = new Dictionary<ushort, DigitalIoState?>();

        foreach (var i in _digitalInputs)
            _digitalInputState.Add(i, null);

        _digitalOutputState = new Dictionary<ushort, DigitalIoState?>();

        foreach (var i in _digitalOutputs)
            _digitalOutputState.Add(i, null);

        _analogInputState = new Dictionary<ushort, AnalogIoState?>();

        foreach (var i in _analogInputs)
            _analogInputState.Add(i, null);

        _analogOutputState = new Dictionary<ushort, AnalogIoState?>();

        foreach (var i in _analogOutputs)
            _analogOutputState.Add(i, null);

        _deviceLock = new SemaphoreSlim(1, 1);
        _connectFactory = connectFactory;
        _connectAction =
            new Func<Task<IDisposableIOBus>>(() =>
                _connectFactory.ConnectAsync(
                "10.1.129.100",
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(1),
                new Dictionary<ushort, AnalogInputConfig>(),
                new Dictionary<ushort, AnalogMeasurementRange>(),
                ct: _ctSrc.Token));

        _connectionLoop = StartConnectionLoop(_ctSrc.Token);
    }

    private readonly Dictionary<ushort, DigitalIoState?> _digitalInputState;
    public IReadOnlyDictionary<ushort, DigitalIoState?> DigitalInputState => _digitalInputState;
    private readonly Dictionary<ushort, DigitalIoState?> _digitalOutputState;
    public IReadOnlyDictionary<ushort, DigitalIoState?> DigitalOutputState => _digitalOutputState;

    private readonly Dictionary<ushort, AnalogIoState?> _analogInputState;
    public IReadOnlyDictionary<ushort, AnalogIoState?> AnalogInputState => _analogInputState;
    private readonly Dictionary<ushort, AnalogIoState?> _analogOutputState;
    public IReadOnlyDictionary<ushort, AnalogIoState?> AnalogOutputState => _analogOutputState;


    private Task StartConnectionLoop(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_phoenix == null || !await _phoenix.IsConnectedAsync(ct))
                    {
                        await _deviceLock.WaitAsync(ct);

                        try
                        {
                            _phoenix?.Dispose();
                            _phoenix = await _connectAction();
                        }
                        finally
                        {
                            _deviceLock.Release();
                        }
                    }

                    var dInputs = await _phoenix.ReadDigitalInputsAsync(_digitalInputs, ct);

                    foreach (var kvp in dInputs)
                        _digitalInputState[kvp.Key] = new DigitalIoState(kvp.Value, DateTime.UtcNow);

                    var dOutputs = await _phoenix.ReadDigitalOutputsAsync(_digitalOutputs, ct);

                    foreach (var kvp in dOutputs)
                        _digitalOutputState[kvp.Key] = new DigitalIoState(kvp.Value, DateTime.UtcNow);

                    var aInputs = await _phoenix.ReadAnalogInputsAsync(_analogInputs, ct);

                    foreach (var kvp in aInputs)
                        _analogInputState[kvp.Key] = new AnalogIoState(kvp.Value, DateTime.UtcNow);

                    var aOutputs = await _phoenix.ReadAnalogOutputsAsync(_analogOutputs, ct);

                    foreach (var kvp in aOutputs)
                        _analogOutputState[kvp.Key] = new AnalogIoState(kvp.Value, DateTime.UtcNow);
                }
                catch (Exception e)
                {

                }
                finally
                {
                    await Task.Delay(500, ct);
                }
            }

        }, ct);
    }

    public async Task SetDigitalOutputStateAsync(
        ushort index,
         bool state,
         CancellationToken ct)
    {
        await _phoenix.WriteDigitalOutputAsync(index, state, ct);
    }


    #region IDisposable

    protected void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        if (disposing)
        {
            _ctSrc.Cancel();
            _connectionLoop.Wait(3000);
            _phoenix?.Dispose();
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