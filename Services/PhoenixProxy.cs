using System.Net.Sockets;
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
    private readonly ILogger<PhoenixProxy> _logger;
    private readonly PauseContainer _pauseCont;

    public PhoenixProxy(
        ITcpIOBusConnectionFactory connectFactory,
        ILogger<PhoenixProxy> logger,
        PauseContainer pauseCont)
    {
        _pauseCont = pauseCont;
        _logger = logger;
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

        var aiconfig =
            new Dictionary<ushort, AnalogInputConfig>()
            {
                { 1, new AnalogInputConfig(AnalogMeasurementRange.FourToTwentyMilliamps, SampleAverageAmount.Four) },
                { 2, new AnalogInputConfig(AnalogMeasurementRange.FourToTwentyMilliamps, SampleAverageAmount.Four) },
                { 3, new AnalogInputConfig(AnalogMeasurementRange.FourToTwentyMilliamps, SampleAverageAmount.Four) },
                { 4, new AnalogInputConfig(AnalogMeasurementRange.FourToTwentyMilliamps, SampleAverageAmount.Four) }
            };

        var aoConfig =
            new Dictionary<ushort, AnalogMeasurementRange>()
            {
                { 1,  AnalogMeasurementRange.FourToTwentyMilliamps },
                { 2,  AnalogMeasurementRange.FourToTwentyMilliamps }
            };

        _connectAction =
            new Func<Task<IDisposableIOBus>>(async () =>
            {
                string host = "192.168.2.50";
                int port = 502;

                bool connected = false;
                var timeout = TimeSpan.FromSeconds(10);

                while (!connected)
                {
                    await _pauseCont.WaitIfPausedAsync(_ctSrc.Token);

                    using (var ctSrc = new CancellationTokenSource())
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ctSrc.Token, _ctSrc.Token))
                    using (var testClient = new TcpClient())
                    {
                        ctSrc.CancelAfter(timeout);

                        try
                        {
                            _logger.LogInformation($"Attempting to test phoenix connection using tcp client. IP: {host}");
                            await testClient.ConnectAsync(host, port, linked.Token);
                            _logger.LogInformation($"Connected using test client now closing connection. IP: {host}");
                            connected = true;
                            testClient.Close();
                        }
                        catch (Exception e)
                        {
                            if (e is TaskCanceledException || e is OperationCanceledException)
                                _logger.LogError(e, $"Error in {nameof(PhoenixProxy)}. Timed out({timeout.TotalSeconds} seconds) trying to test phoenix connection");
                            else
                                _logger.LogError(e, $"Error in {nameof(PhoenixProxy)} in ctor. Error message: {e.Message}");

                            await Task.Delay(3000, _ctSrc.Token);
                        }
                    }
                }

                _logger.LogInformation("Attempting to connect to phoenix using modbus client");

                return
                    await _connectFactory.ConnectAsync(
                        host,
                        TimeSpan.FromSeconds(3),
                        TimeSpan.FromSeconds(1),
                        aiconfig,
                        aoConfig,
                        port,
                        ct: _ctSrc.Token);
            });

        _connectionLoop = StartReadLoop(_ctSrc.Token);
    }

    private readonly Dictionary<ushort, DigitalIoState?> _digitalInputState;
    public IReadOnlyDictionary<ushort, DigitalIoState?> DigitalInputState => _digitalInputState;
    private readonly Dictionary<ushort, DigitalIoState?> _digitalOutputState;
    public IReadOnlyDictionary<ushort, DigitalIoState?> DigitalOutputState => _digitalOutputState;

    private readonly Dictionary<ushort, AnalogIoState?> _analogInputState;
    public IReadOnlyDictionary<ushort, AnalogIoState?> AnalogInputState => _analogInputState;
    private readonly Dictionary<ushort, AnalogIoState?> _analogOutputState;
    public IReadOnlyDictionary<ushort, AnalogIoState?> AnalogOutputState => _analogOutputState;


    private Task StartReadLoop(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await _pauseCont.WaitIfPausedAsync(ct);

                    if (_phoenix == null || !await _phoenix.IsConnectedAsync(ct))
                    {
                        await _deviceLock.WaitAsync(ct);

                        try
                        {
                            _phoenix?.Dispose();
                            _logger.LogInformation("Attempting to connect to phoenix..");
                            _phoenix = await _connectAction();
                            _logger.LogInformation("Successfully connected to phoenix");
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
                    _logger.LogError(e, $"Error in {nameof(PhoenixProxy)} in {nameof(StartReadLoop)}: {e.Message}");
                }
                finally
                {
                    await Task.Delay(50, ct);
                }
            }

        }, ct);
    }

    public async Task SetDigitalOutputStateAsync(
        ushort index,
        bool state,
        CancellationToken ct)
    {
        if (_phoenix == null)
            throw new Exception($"No phoenix connection");

        await _phoenix.WriteDigitalOutputAsync(index, state, ct);
    }

    public async Task SetAnalogOutputStateAsync(
        ushort index,
        double state,
        CancellationToken ct)
    {
        if (_phoenix == null)
            throw new Exception($"No phoenix connection");

        await _phoenix.WriteAnalogOutputAsync(index, state, ct);
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
            _deviceLock?.Dispose();
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