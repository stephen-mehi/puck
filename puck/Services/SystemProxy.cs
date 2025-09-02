using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using puck.Services.IoBus;
using puck.Services.PID;
using Puck.Models;
using Puck.Services.TemperatureController;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Puck.Services
{
    public class RunParameters
    {
        public double InitialPumpSpeed { get; set; }
        public int GroupHeadTemperatureFarenheit { get; set; }
        public int ThermoblockTemperatureFarenheit { get; set; }
        public int PreExtractionTargetTemperatureFarenheit { get; set; }
        public double ExtractionWeightGrams { get; set; }
        public int MaxExtractionSeconds { get; set; }
        public double TargetPressureBar { get; set; }
    }


    public enum RunCompletionStatus
    {
        NONE = 0,
        SUCCEEDED = 1,
        FAILED = 2,
        PARTIALLY_SUCCEEDED = 3
    }

    public enum EventStatusLevel
    {
        NONE = 0,
        INFORMATION = 1,
        WARNING = 2,
        ERROR = 3
    }


    public struct RunResult
    {
        public string RunId { get; set; }
        public RunParameters? RunParameters { get; set; }

        public DateTime? StartTimeUTC { get; set; }
        public DateTime? EndTimeUTC { get; set; }

        public List<ProcessDeviceState>? Events { get; set; }
        public RunCompletionStatus CompletionStatus { get; set; }

    }

    public enum RunState
    {
        None = 0,
        Run = 1,
        Idle = 2
    }

    public enum ValveState
    {
        None = 0,
        Open = 1,
        Closed = 2
    }

    public enum TemperatureControllerId
    {
        None = 0,
        ThermoBlock = 1,
        GroupHead = 2
    }

    public enum PressureUnit
    {
        Bar = 0,
        Psi = 1,
        KPa = 2
    }


    // Add delta/action types and ValveType enum
    public enum ValveType { GroupHead, Backflush, Recirculation }

    public abstract record SystemStateDelta;
    public record ValveStateChanged(ValveType Valve, ValveState NewState) : SystemStateDelta;
    public record TemperatureChanged(TemperatureControllerId Controller, double? NewValue) : SystemStateDelta;
    public record PumpSpeedChanged(double? NewValue) : SystemStateDelta;
    public record PausedChanged(bool IsPaused) : SystemStateDelta;
    public record RunStateChanged(RunState NewState) : SystemStateDelta;
    public record TemperatureSetpointChanged(TemperatureControllerId Controller, double? NewSetpoint) : SystemStateDelta;
    public record StatusMessageChanged(string Message) : SystemStateDelta;
    public record ConnectionChanged(bool IsConnected) : SystemStateDelta;
    public record PidParametersChanged(
        double Kp, double Ki, double Kd, double N,
        double OutputUpper, double OutputLower
    ) : SystemStateDelta;
    public record PressureChanged(double? NewValue) : SystemStateDelta;
    public record ExtractionWeightChanged(double? Weight) : SystemStateDelta;
    // Add more as needed

    public class SystemProxy : IDisposable
    {
        private readonly ILogger<SystemService> _logger;
        private readonly IPhoenixProxy _ioProxy;
        private readonly IReadOnlyDictionary<TemperatureControllerId, ITemperatureController> _tempProxy;
        private readonly CancellationTokenSource _ctSrc;
        private readonly SemaphoreSlim _systemLock;
        private readonly SemaphoreSlim _runLock;
        private readonly SemaphoreSlim _runScanLock;
        private readonly PauseContainer _pauseCont;
        private readonly PID _pid;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly RunResultRepo _runRepo;

        private bool _isDisposed;
        private IDisposable? _connectionSub;
        private readonly SemaphoreSlim _pressureLoopLock;
        private CancellationTokenSource? _pressureLoopCts;

        // IO Mapping (configurable)
        private readonly ushort _recircValveIO;
        private readonly ushort _groupheadValveIO;
        private readonly ushort _backflushValveIO;
        private readonly ushort _runStatusOutputIO;
        private readonly ushort _runStatusInputIO;
        private readonly ushort _pumpSpeedIO;
        private readonly ushort _pressureIO;

        // Timing and control (configurable)
        private readonly int _recircValveOpenDelayMs;
        private readonly int _initialPumpSpeedDelayMs;
        private readonly int _tempSettleTolerance;
        private readonly int _tempSettleTimeoutSec;
        private readonly int _pidLoopDelayMs;
        private readonly int _mainScanLoopDelayMs;
        private readonly int _runStateMonitorDelayMs;
        private readonly double _pumpStopValue;
        private readonly int _setAllIdleRecircOpenDelayMs;
        // Pressure IO configuration
        private readonly PressureUnit _pressureUnit;
        private readonly double _sensorMinPressurePsi; // in PSI
        private readonly double _sensorMaxPressurePsi; // in PSI
        private readonly double _sensorMinCurrentmA;
        private readonly double _sensorMaxCurrentmA;

        private readonly Subject<SystemStateDelta> _stateDeltaSubject = new();
        private readonly BehaviorSubject<ProcessDeviceState> _stateSubject;
        public IObservable<ProcessDeviceState> StateObservable => _stateSubject.AsObservable();


        // Overload: construct using options bag for all defaults
        public SystemProxy(
            ILogger<SystemService> logger,
            IPhoenixProxy ioProxy,
            TemperatureControllerContainer tempControllers,
            PauseContainer pauseCont,
            PID pid,
            IServiceScopeFactory scopeFactory,
            RunResultRepo runRepo,
            SystemProxyConfiguration options)
        {
            _runRepo = runRepo;
            _scopeFactory = scopeFactory;
            _pid = pid;
            _pauseCont = pauseCont;
            _logger = logger;
            _ioProxy = ioProxy;
            _tempProxy = tempControllers.TemperatureControllers;
            _systemLock = new SemaphoreSlim(1, 1);
            _runLock = new SemaphoreSlim(1, 1);
            _runScanLock = new SemaphoreSlim(1, 1);
            _ctSrc = new CancellationTokenSource();
            _pressureLoopLock = new SemaphoreSlim(1, 1);
            // IO mapping
            _recircValveIO = options.RecircValveIO;
            _groupheadValveIO = options.GroupheadValveIO;
            _backflushValveIO = options.BackflushValveIO;
            _runStatusOutputIO = options.RunStatusOutputIO;
            _runStatusInputIO = options.RunStatusInputIO;
            _pumpSpeedIO = options.PumpSpeedIO;
            _pressureIO = options.PressureIO;
            // Timing and control
            _recircValveOpenDelayMs = options.RecircValveOpenDelayMs;
            _initialPumpSpeedDelayMs = options.InitialPumpSpeedDelayMs;
            _tempSettleTolerance = options.TempSettleTolerance;
            _tempSettleTimeoutSec = options.TempSettleTimeoutSec;
            _pidLoopDelayMs = options.PidLoopDelayMs;
            _mainScanLoopDelayMs = options.MainScanLoopDelayMs;
            _runStateMonitorDelayMs = options.RunStateMonitorDelayMs;
            _pumpStopValue = options.PumpStopValue;
            _setAllIdleRecircOpenDelayMs = options.SetAllIdleRecircOpenDelayMs;
            _pressureUnit = options.PressureUnit;
            _sensorMinPressurePsi = options.SensorMinPressurePsi;
            _sensorMaxPressurePsi = options.SensorMaxPressurePsi;
            _sensorMinCurrentmA = options.SensorMinCurrentmA;
            _sensorMaxCurrentmA = options.SensorMaxCurrentmA;
            _stateSubject = new BehaviorSubject<ProcessDeviceState>(GetInitialProcessDeviceState());
            _stateDeltaSubject.Subscribe(delta =>
            {
                var newState = Reduce(_stateSubject.Value, delta);
                _stateSubject.OnNext(newState);
            });

            // Subscribe to connection state changes
            _connectionSub = _ioProxy.IsConnected.Subscribe(connected =>
            {
                _logger.LogWarning($"PhoenixProxy connection state changed: {(connected ? "Connected" : "Disconnected")}");
                _stateDeltaSubject.OnNext(new ConnectionChanged(connected));
            });
            // Initialize connection state
            _stateDeltaSubject.OnNext(new ConnectionChanged(_ioProxy.IsCurrentlyConnected));
        }

        private RunParameters? GetActiveParametersFromRepository()
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<RunParametersRepo>();
            return repo.GetActiveParameters();
        }

        private ProcessDeviceState GetInitialProcessDeviceState()
        {
            var now = DateTime.UtcNow;
            return new ProcessDeviceState(
                groupHeadTemperature: null,
                thermoblockTemperature: null,
                groupHeadSetpoint: null,
                thermoblockSetpoint: null,
                groupHeadHeaterEnabled: false,
                thermoblockHeaterEnabled: false,
                isIoBusConnected: false,
                groupHeadPressure: null,
                pumpSpeed: null,
                groupHeadValveState: ValveState.None,
                backflushValveState: ValveState.None,
                recirculationValveState: ValveState.None,
                runState: RunState.None,
                isPaused: false,
                runStartTimeUtc: null,
                extractionWeight: null,
                pid_Kp: null,
                pid_Ki: null,
                pid_Kd: null,
                pid_N: null,
                pid_OutputUpperLimit: null,
                pid_OutputLowerLimit: null,
                stateTimestampUtc: now
            );
        }

        private ProcessDeviceState Reduce(ProcessDeviceState oldState, SystemStateDelta delta)
        {
            var now = DateTime.UtcNow;
            return delta switch
            {
                ValveStateChanged(var valve, var newState) => valve switch
                {
                    ValveType.GroupHead => oldState with { GroupHeadValveState = newState, StateTimestampUtc = now, GeneralStatusMessage = $"GroupHead valve set to {newState}" },
                    ValveType.Backflush => oldState with { BackflushValveState = newState, StateTimestampUtc = now, GeneralStatusMessage = $"Backflush valve set to {newState}" },
                    ValveType.Recirculation => oldState with { RecirculationValveState = newState, StateTimestampUtc = now, GeneralStatusMessage = $"Recirculation valve set to {newState}" },
                    _ => oldState
                },
                TemperatureChanged(var controller, var newValue) => controller switch
                {
                    TemperatureControllerId.GroupHead => oldState with { GroupHeadTemperature = newValue, StateTimestampUtc = now, GeneralStatusMessage = $"GroupHead temperature updated to {newValue}" },
                    TemperatureControllerId.ThermoBlock => oldState with { ThermoblockTemperature = newValue, StateTimestampUtc = now, GeneralStatusMessage = $"Thermoblock temperature updated to {newValue}" },
                    _ => oldState
                },
                PumpSpeedChanged(var newValue) => oldState with { PumpSpeed = newValue, StateTimestampUtc = now, GeneralStatusMessage = $"Pump speed set to {newValue}" },
                PausedChanged(var isPaused) => oldState with { IsPaused = isPaused, StateTimestampUtc = now, GeneralStatusMessage = isPaused ? "System paused" : "System unpaused" },
                RunStateChanged(var newState) => oldState with { RunState = newState, StateTimestampUtc = now, GeneralStatusMessage = $"Run state changed to {newState}" },
                TemperatureSetpointChanged(var controller, var newSetpoint) => controller switch
                {
                    TemperatureControllerId.GroupHead => oldState with { GroupHeadSetpoint = newSetpoint, StateTimestampUtc = now, GeneralStatusMessage = $"GroupHead setpoint set to {newSetpoint}" },
                    TemperatureControllerId.ThermoBlock => oldState with { ThermoblockSetpoint = newSetpoint, StateTimestampUtc = now, GeneralStatusMessage = $"WRITE: Thermoblock setpoint set to {newSetpoint}" },
                    _ => oldState
                },
                StatusMessageChanged(var msg) => oldState with { GeneralStatusMessage = msg, StateTimestampUtc = now },
                ConnectionChanged(var isConnected) => oldState with { IsIoBusConnected = isConnected, StateTimestampUtc = now, GeneralStatusMessage = $"PhoenixProxy connection {(isConnected ? "established" : "lost")}" },
                PidParametersChanged(var kp, var ki, var kd, var n, var outU, var outL)
                    => oldState with
                    {
                        PID_Kp = kp,
                        PID_Ki = ki,
                        PID_Kd = kd,
                        PID_N = n,
                        PID_OutputUpperLimit = outU,
                        PID_OutputLowerLimit = outL,
                        StateTimestampUtc = now,
                        GeneralStatusMessage = $"PID parameters updated: Kp={kp} Ki={ki} Kd={kd} N={n} OutU={outU} OutL={outL}"
                    },
                PressureChanged(var newValue) => oldState with { GroupHeadPressure = newValue, StateTimestampUtc = now, GeneralStatusMessage = $"GroupHead pressure updated to {newValue}" },
                ExtractionWeightChanged(var weight) => oldState with { ExtractionWeight = weight, StateTimestampUtc = now, GeneralStatusMessage = $"Extraction weight updated to {weight}" },
                _ => oldState
            };
        }

        public async Task StartRunScan(CancellationToken ct)
        {
            // Ensure only one scan can run at a time by acquiring the run scan lock (non-blocking)
            if (!await _runScanLock.WaitAsync(0))
                throw new Exception("Cannot start run scan if already started");

            // Combine the external cancellation token with the internal one for coordinated cancellation
            using var combineCtSrc = CancellationTokenSource.CreateLinkedTokenSource(ct, _ctSrc.Token);

            // Used to cancel the current run if the system goes idle
            CancellationTokenSource runStopSrc = new CancellationTokenSource();

            // Main scan task: handles the espresso run logic
            var scanTask = Task.Run(async () =>
            {
                RunResult currentRes;

                // Before entering the main scan loop, declare:
                System.IDisposable? stateSub = null;
                List<ProcessDeviceState> runStateHistory = new();
                bool accumulating = false;

                // Main scan loop: runs until cancelled
                while (!combineCtSrc.IsCancellationRequested)
                {
                    try
                    {
                        // Pause logic: will block here if pause is requested
                        await _pauseCont.WaitIfPausedAsync(ct);

                        // Only proceed if the system is in Run state
                        if (GetRunState() == RunState.Run)
                        {
                            if (!accumulating)
                            {
                                // Start accumulating state changes
                                accumulating = true;
                                runStateHistory.Clear();
                                stateSub = StateObservable.Subscribe(state =>
                                {
                                    // Optionally deduplicate consecutive identical states
                                    if (runStateHistory.Count == 0 || !state.Equals(runStateHistory[^1]))
                                        runStateHistory.Add(state);

                                });
                            }

                            var starttime = DateTime.UtcNow;
                            var runParams = GetActiveParametersFromRepository();

                            // Initialize run result tracking
                            currentRes = new RunResult();
                            currentRes.Events = new List<ProcessDeviceState>();
                            currentRes.CompletionStatus = RunCompletionStatus.NONE;
                            currentRes.StartTimeUTC = starttime;
                            currentRes.RunId =
                                $"RUN_{starttime.Month}_{starttime.Day}_{starttime.Year}_{starttime.Hour}_{starttime.Minute}_{starttime.Second}_{starttime.Ticks}";

                            currentRes.RunParameters = runParams;

                            _runRepo.SetLatestRunResult(currentRes);

                            // Acquire both run and system locks for exclusive access to system resources
                            await _runLock.WaitAsync(combineCtSrc.Token);
                            await _systemLock.WaitAsync(combineCtSrc.Token);

                            // Create a combined cancellation token for the run
                            using var allCtSrc =
                                CancellationTokenSource
                                .CreateLinkedTokenSource(runStopSrc.Token, combineCtSrc.Token);

                            try
                            {
                                // Emit PID parameters at start
                                _stateDeltaSubject.OnNext(new PidParametersChanged(
                                    _pid.Kp,
                                    _pid.Ki,
                                    _pid.Kd,
                                    _pid.N,
                                    _pid.OutputUpperLimit,
                                    _pid.OutputLowerLimit
                                ));

                                // --- Espresso control logic begins ---

                                // Close backflush valve
                                await SetBackFlushValveStateClosedInternalAsync(allCtSrc.Token);

                                // Close grouphead valve
                                await SetGroupHeadValveStateClosedInternalAsync(allCtSrc.Token);

                                // Open recirculation valve
                                await SetRecirculationValveStateOpenInternalAsync(allCtSrc.Token);
                                await Task.Delay(_recircValveOpenDelayMs, allCtSrc.Token);

                                // Set initial pump speed
                                await ApplyPumpSpeedInternalAsync(runParams.InitialPumpSpeed, allCtSrc.Token);
                                await Task.Delay(TimeSpan.FromMilliseconds(_initialPumpSpeedDelayMs), allCtSrc.Token);

                                // Set heater enabled and wait for temperature
                                var groupHeadHeatUpTask =
                                    SetTemperatureSynchronouslyAsync(
                                        TemperatureControllerId.GroupHead,
                                        runParams.GroupHeadTemperatureFarenheit,
                                        TimeSpan.FromSeconds(_tempSettleTimeoutSec),
                                        2.0,
                                        allCtSrc.Token);

                                var thermoblockHeatUp =
                                    SetTemperatureSynchronouslyAsync(
                                        TemperatureControllerId.ThermoBlock,
                                        runParams.ThermoblockTemperatureFarenheit,
                                        TimeSpan.FromSeconds(_tempSettleTimeoutSec),
                                        2.0,
                                        allCtSrc.Token);

                                await Task.WhenAll(groupHeadHeatUpTask, thermoblockHeatUp);

                                // Tare scale (TODO)
                                // Open grouphead valve
                                await SetGroupHeadValveStateOpenInternalAsync(allCtSrc.Token);
                                // Close recirculation valve
                                await SetRecirculationValveStateClosedInternalAsync(allCtSrc.Token);
                                // Start PID pressure loop

                                double weight = 0;
                                DateTime prevTime = DateTime.UtcNow;

                                // Extraction loop: runs until target weight or timeout
                                while (weight < runParams.ExtractionWeightGrams)
                                {
                                    // (Consider adding pause check here for better responsiveness)

                                    //Timeout check: abort if extraction takes too long
                                    if ((DateTime.UtcNow - prevTime) > TimeSpan.FromSeconds(runParams.MaxExtractionSeconds))
                                    {
                                        string msg =
                                            $"Timeout occurred while running. Extraction exceeded timeout: {runParams.MaxExtractionSeconds} so exiting extraction routine";

                                        _stateDeltaSubject.OnNext(new StatusMessageChanged(msg));
                                        throw new Exception(msg);
                                    }

                                    //var extractionWeight = await _ioProxy.GetScaleWeightAsync(allCtSrc.Token);

                                    weight += 1; // Simulate weight increment for testing

                                    _stateDeltaSubject.OnNext(new ExtractionWeightChanged(weight));
                                    var pressure = GetGroupHeadPressure();
                                    _stateDeltaSubject.OnNext(new PressureChanged(pressure));

                                    if (!pressure.HasValue)
                                    {
                                        string msg =
                                            $"Failed to get grouphead pressure so exiting extraction routine";

                                        _stateDeltaSubject.OnNext(new StatusMessageChanged(msg));
                                        throw new Exception(msg);
                                    }

                                    var groupTempLoop = await _tempProxy[TemperatureControllerId.GroupHead].GetProcessValueAsync(allCtSrc.Token);
                                    var thermoblockTempLoop = await _tempProxy[TemperatureControllerId.ThermoBlock].GetProcessValueAsync(allCtSrc.Token);
                                    _stateDeltaSubject.OnNext(new TemperatureChanged(TemperatureControllerId.GroupHead, groupTempLoop));
                                    _stateDeltaSubject.OnNext(new TemperatureChanged(TemperatureControllerId.ThermoBlock, thermoblockTempLoop));

                                    // Wait between PID iterations, responsive to cancellation
                                    await Task.Delay(TimeSpan.FromMilliseconds(_pidLoopDelayMs), allCtSrc.Token);
                                    var currTime = DateTime.UtcNow;
                                    var output = _pid.PID_iterate(runParams.TargetPressureBar, pressure.Value, currTime - prevTime);
                                    prevTime = currTime;
                                    await ApplyPumpSpeedInternalAsync(output, allCtSrc.Token);
                                }

                                currentRes.CompletionStatus = RunCompletionStatus.SUCCEEDED;
                            }
                            catch (Exception)
                            {
                                currentRes.CompletionStatus = RunCompletionStatus.FAILED;
                                throw;
                            }
                            finally
                            {
                                // Cleanup steps: attempt to safely stop/idle all systems
                                try { await StopPumpInternalAsync(allCtSrc.Token); } catch { }
                                try { await _tempProxy[TemperatureControllerId.ThermoBlock].DisableControlLoopAsync(allCtSrc.Token); } catch { }
                                try { await _tempProxy[TemperatureControllerId.GroupHead].DisableControlLoopAsync(allCtSrc.Token); } catch { }
                                try { await SetGroupHeadValveStateClosedInternalAsync(allCtSrc.Token); } catch { }
                                try { await SetAllIdleInternalAsync(allCtSrc.Token); } catch { }

                                currentRes.EndTimeUTC = DateTime.UtcNow;

                                _stateDeltaSubject.OnNext(new StatusMessageChanged("Completed extraction run "));
                                // Emit PID parameters at end
                                _stateDeltaSubject.OnNext(new PidParametersChanged(
                                    _pid.Kp, _pid.Ki, _pid.Kd, _pid.N,
                                    _pid.OutputUpperLimit, _pid.OutputLowerLimit
                                ));

                                if (stateSub != null)
                                {
                                    stateSub.Dispose();
                                    stateSub = null;
                                }
                                currentRes.Events.AddRange(runStateHistory);

                                _runRepo.SetLatestRunResult(currentRes);

                                runStateHistory = new();
                                accumulating = false;

                                try { await SetRunStateIdleAsync(allCtSrc.Token); } catch { }

                                // Release locks (note: if not acquired, this will throw)
                                _runLock.Release();
                                _systemLock.Release();
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        // Log any errors in the scan loop
                        _logger.LogError(e, $"Failed in {nameof(StartRunScan)} within {nameof(SystemProxy)}: {e.Message}");
                    }
                    finally
                    {
                        // Main scan loop delay
                        await Task.Delay(_mainScanLoopDelayMs);
                    }
                }
            });

            // Task to monitor run state and cancel the run if system goes idle
            var runStateScanTask = Task.Run(async () =>
            {
                try
                {
                    // Run state monitoring loop
                    while (!combineCtSrc.IsCancellationRequested)
                    {
                        try
                        {
                            // If run lock is not available and system is idle, cancel the run
                            if (_runLock.CurrentCount == 0)
                            {
                                if (GetRunState() == RunState.Idle)
                                {
                                    _logger.LogInformation("Cancelling mid-process run");

                                    // Cancel run process 
                                    runStopSrc.Cancel();
                                    runStopSrc.Dispose();
                                    runStopSrc = new CancellationTokenSource();

                                    //and wait for it to release run lock
                                    await _runLock.WaitAsync(combineCtSrc.Token);
                                    _runLock.Release();
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            // Log any errors in the run state monitor
                            _logger.LogError(e, $"Failed in {nameof(StartRunScan)} within {nameof(SystemProxy)}: {e.Message}");
                        }
                        finally
                        {
                            // Run state monitor delay
                            await Task.Delay(_runStateMonitorDelayMs);
                        }
                    }
                }
                finally
                {
                    // Ensure the run is cancelled and resources are cleaned up
                    runStopSrc.Cancel();
                    runStopSrc.Dispose();

                    try
                    {
                        await scanTask;
                    }
                    finally
                    {
                        // runStopSrc already disposed
                    }
                }
            });

            // Wait for both tasks to complete
            await Task.WhenAll(scanTask, runStateScanTask);
        }

        private AnalogIoState? GetAnalogInputState(ushort index)
        {
            if (!_ioProxy.AnalogInputState.TryGetValue(index, out var val))
                throw new Exception($"Error in {nameof(GetAnalogInputState)} within {nameof(SystemProxy)}. No analog input found with index {index}");

            return val;
        }

        private AnalogIoState? GetAnalogOutputState(ushort index)
        {
            if (!_ioProxy.AnalogOutputState.TryGetValue(index, out var val))
                throw new Exception($"Error in {nameof(GetAnalogInputState)} within {nameof(SystemProxy)}. No analog output found with index {index}");

            return val;
        }

        private ValveState GetValveState(ushort index)
        {
            if (!_ioProxy.DigitalOutputState.TryGetValue(index, out var val))
                throw new Exception($"Error in {nameof(GetValveState)} within {nameof(SystemProxy)}. No digital output found with index {index}");

            if (!val.HasValue)
                return ValveState.None;

            var runState = val.Value.State ? ValveState.Open : ValveState.Closed;

            return runState;
        }

        public RunState GetRunState()
        {
            //TODO: CHANGE THIS BACK TO DIGITAL INPUT ONCE CONNECTED TO HARDWARE
            if (!_ioProxy.DigitalOutputState.TryGetValue(_runStatusOutputIO, out var val))
                throw new Exception($"Error in {nameof(GetRunState)} within {nameof(SystemProxy)}. No digital input found with index {_runStatusOutputIO}");

            if (!val.HasValue)
                return RunState.None;

            var runState = val.Value.State ? RunState.Run : RunState.Idle;

            return runState;
        }


        public ValveState GetRecirculationValveState()
        {
            var state = GetValveState(_recircValveIO);
            return state;
        }

        public ValveState GetGroupHeadValveState()
        {
            var state = GetValveState(_groupheadValveIO);
            return state;
        }

        public ValveState GetBackFlushValveState()
        {
            var state = GetValveState(_backflushValveIO);
            return state;
        }


        public double? GetPumpSpeedSetting()
        {
            var pumpSpeed = GetAnalogOutputState(_pumpSpeedIO);

            return pumpSpeed.HasValue ? pumpSpeed.Value.State : null;
        }

        public double? GetGroupHeadPressure()
        {
            var press = GetAnalogInputState(_pressureIO);
            if (!press.HasValue)
                return null;

            double raw = press.Value.State;

            // Interpret analog input as current in mA and convert 4-20mA to pressure (Bar), then to target unit
            double currentmA = raw;
            // Clip current to expected range
            double clipped = Math.Max(_sensorMinCurrentmA, Math.Min(_sensorMaxCurrentmA, currentmA));
            double normalized = (clipped - _sensorMinCurrentmA) / (_sensorMaxCurrentmA - _sensorMinCurrentmA);
            normalized = Math.Max(0.0, Math.Min(1.0, normalized));
            double pressurePsi = _sensorMinPressurePsi + normalized * (_sensorMaxPressurePsi - _sensorMinPressurePsi);
            return ConvertPsiToUnit(pressurePsi, _pressureUnit);
        }

        private static double ConvertPsiToUnit(double valuePsi, PressureUnit unit)
        {
            return unit switch
            {
                PressureUnit.Psi => valuePsi,
                PressureUnit.Bar => valuePsi / 14.5037738,
                PressureUnit.KPa => valuePsi * 6.894757293168,
                _ => valuePsi
            };
        }


        public Task<double> GetProcessTemperatureAsync(
            TemperatureControllerId controllerId,
            CancellationToken ct)
        {
            return _tempProxy[controllerId].GetProcessValueAsync(ct);

        }

        public Task<double> GetSetPointTemperatureAsync(
            TemperatureControllerId controllerId,
            CancellationToken ct)
        {
            return _tempProxy[controllerId].GetSetValueAsync(ct);
        }

        private async Task ExecuteSystemActionAsync(
            Func<Task> action,
            CancellationToken ct)
        {
            if (!_runLock.Wait(0))
            {
                throw new Exception("Cannot execute operation while run is in process");
            }
            else
            {
                try
                {
                    await _systemLock.WaitAsync(ct);

                    try
                    {
                        await action();
                    }
                    finally
                    {
                        _systemLock.Release();
                    }
                }
                finally
                {
                    _runLock.Release();
                }
            }
        }

        private async Task RunInternalAsync(CancellationToken ct)
        {
            await _ioProxy.SetDigitalOutputStateAsync(_runStatusOutputIO, true, ct);
            _stateDeltaSubject.OnNext(new RunStateChanged(RunState.Run));
        }

        public async Task RunAsync(
            RunParameters? runParams,
            CancellationToken ct)
        {
            var effectiveParams = runParams ?? GetActiveParametersFromRepository();
            if (effectiveParams == null)
                throw new Exception("No active run parameters are set");
            await ExecuteSystemActionAsync(() => RunInternalAsync(ct), ct);
        }

        public async Task SetRunStateIdleAsync(CancellationToken ct)
        {
            await _ioProxy.SetDigitalOutputStateAsync(_runStatusOutputIO, false, ct);
            _stateDeltaSubject.OnNext(new RunStateChanged(RunState.Idle));
        }

        private async Task SetTemperatureSetpointInternalAsync(int setpoint, TemperatureControllerId controllerId, CancellationToken ct)
        {
            await _tempProxy[controllerId].SetSetPointAsync(setpoint, ct);
            _stateDeltaSubject.OnNext(new TemperatureSetpointChanged(controllerId, setpoint));
        }

        public Task SetTemperatureSetpointAsync(int setpoint, TemperatureControllerId controllerId, CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetTemperatureSetpointInternalAsync(setpoint, controllerId, ct), ct);
        }

        private async Task ApplyPumpSpeedInternalAsync(double speed, CancellationToken ct)
        {
            await _ioProxy.SetAnalogOutputStateAsync(_pumpSpeedIO, speed, ct);
            _stateDeltaSubject.OnNext(new PumpSpeedChanged(speed));
        }

        public Task ApplyPumpSpeedAsync(double speed, CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => ApplyPumpSpeedInternalAsync(speed, ct), ct);
        }

        private async Task StopPumpInternalAsync(CancellationToken ct)
        {
            await _ioProxy.SetAnalogOutputStateAsync(_pumpSpeedIO, _pumpStopValue, ct);
            _stateDeltaSubject.OnNext(new PumpSpeedChanged(_pumpStopValue));
        }

        public Task StopPumpAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => StopPumpInternalAsync(ct), ct);
        }

        // --- Pressure control loop (public API) ---
        public async Task StartPressureControlAsync(double targetPressurePsi, double minPumpSpeed, double maxPumpSpeed, CancellationToken ct)
        {
            if (targetPressurePsi < 0) throw new ArgumentOutOfRangeException(nameof(targetPressurePsi));
            if (maxPumpSpeed <= minPumpSpeed) throw new ArgumentException("maxPumpSpeed must be > minPumpSpeed");

            await _pressureLoopLock.WaitAsync(ct);
            try
            {
                if (_pressureLoopCts != null)
                    throw new Exception("Pressure control loop already running");

                _pressureLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _ctSrc.Token);
                var loopCt = _pressureLoopCts.Token;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Clamp PID output to requested pump window
                        double prevUpper = _pid.OutputUpperLimit;
                        double prevLower = _pid.OutputLowerLimit;
                        _pid.SetOutputLimits(maxPumpSpeed, minPumpSpeed);
                        _stateDeltaSubject.OnNext(new PidParametersChanged(_pid.Kp, _pid.Ki, _pid.Kd, _pid.N, _pid.OutputUpperLimit, _pid.OutputLowerLimit));

                        DateTime prevTime = DateTime.UtcNow;
                        await ApplyPumpSpeedInternalAsync(minPumpSpeed, loopCt);

                        while (!loopCt.IsCancellationRequested)
                        {
                            var now = DateTime.UtcNow;
                            var p = GetGroupHeadPressure();
                            if (!p.HasValue)
                            {
                                await Task.Delay(100, loopCt);
                                prevTime = now;
                                continue;
                            }

                            double u = _pid.PID_iterate(targetPressurePsi, p.Value, now - prevTime);
                            prevTime = now;
                            double clampedU = Math.Max(minPumpSpeed, Math.Min(maxPumpSpeed, u));
                            await ApplyPumpSpeedInternalAsync(clampedU, loopCt);
                            await Task.Delay(_pidLoopDelayMs, loopCt);
                        }

                        // Restore previous limits
                        _pid.SetOutputLimits(prevUpper, prevLower);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Pressure control loop errored");
                        throw;
                    }
                    finally
                    {
                        await StopPumpInternalAsync(CancellationToken.None); 
                    }
                }, loopCt);
            }
            finally
            {
                _pressureLoopLock.Release();
            }
        }

        public async Task StopPressureControlAsync(CancellationToken ct)
        {
            await _pressureLoopLock.WaitAsync(ct);
            try
            {
                if (_pressureLoopCts == null) return;
                _pressureLoopCts.Cancel();
                _pressureLoopCts.Dispose();
                _pressureLoopCts = null;
            }
            finally
            {
                _pressureLoopLock.Release();
            }
        }


        //GROUPHEAD
        private async Task SetGroupHeadValveStateOpenInternalAsync(CancellationToken ct)
        {
            await _ioProxy.SetDigitalOutputStateAsync(_groupheadValveIO, true, ct);
            _stateDeltaSubject.OnNext(new ValveStateChanged(ValveType.GroupHead, ValveState.Open));
        }

        public Task SetGroupHeadValveStateOpenAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetGroupHeadValveStateOpenInternalAsync(ct), ct);
        }

        private async Task SetGroupHeadValveStateClosedInternalAsync(CancellationToken ct)
        {
            await _ioProxy.SetDigitalOutputStateAsync(_groupheadValveIO, false, ct);
            _stateDeltaSubject.OnNext(new ValveStateChanged(ValveType.GroupHead, ValveState.Closed));
        }

        public Task SetGroupHeadValveStateClosedAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetGroupHeadValveStateClosedInternalAsync(ct), ct);
        }

        //BACKFLUSH
        private async Task SetBackFlushValveStateOpenInternalAsync(CancellationToken ct)
        {
            await _ioProxy.SetDigitalOutputStateAsync(_backflushValveIO, true, ct);
            _stateDeltaSubject.OnNext(new ValveStateChanged(ValveType.Backflush, ValveState.Open));
        }

        public Task SetBackFlushValveStateOpenAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetBackFlushValveStateOpenInternalAsync(ct), ct);
        }

        private async Task SetBackFlushValveStateClosedInternalAsync(CancellationToken ct)
        {
            await _ioProxy.SetDigitalOutputStateAsync(_backflushValveIO, false, ct);
            _stateDeltaSubject.OnNext(new ValveStateChanged(ValveType.Backflush, ValveState.Closed));
        }

        public Task SetBackFlushValveStateClosedAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetBackFlushValveStateClosedInternalAsync(ct), ct);
        }


        //RECIRC
        private async Task SetRecirculationValveStateOpenInternalAsync(CancellationToken ct)
        {
            await _ioProxy.SetDigitalOutputStateAsync(_recircValveIO, true, ct);
            _stateDeltaSubject.OnNext(new ValveStateChanged(ValveType.Recirculation, ValveState.Open));
        }

        public Task SetRecirculationValveStateOpenAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetRecirculationValveStateOpenInternalAsync(ct), ct);
        }

        private async Task SetRecirculationValveStateClosedInternalAsync(CancellationToken ct)
        {
            await _ioProxy.SetDigitalOutputStateAsync(_recircValveIO, false, ct);
            _stateDeltaSubject.OnNext(new ValveStateChanged(ValveType.Recirculation, ValveState.Closed));
        }

        public Task SetRecirculationValveStateClosedAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetRecirculationValveStateClosedInternalAsync(ct), ct);
        }

        public async Task SetAllIdleInternalAsync(CancellationToken ct)
        {
            await _tempProxy[TemperatureControllerId.GroupHead].DisableControlLoopAsync(ct);
            await _tempProxy[TemperatureControllerId.ThermoBlock].DisableControlLoopAsync(ct);
            await StopPumpInternalAsync(ct);
            await SetRecirculationValveStateOpenInternalAsync(ct);
            await Task.Delay(_setAllIdleRecircOpenDelayMs, ct);
            await SetGroupHeadValveStateClosedInternalAsync(ct);
            await SetRecirculationValveStateClosedInternalAsync(ct);
            await SetBackFlushValveStateClosedInternalAsync(ct);
        }

        public Task SetAllIdleAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetAllIdleInternalAsync(ct), ct);
        }

        public async Task<SystemState> GetSystemStateAsync()
        {
            // Gather temperature controller states
            double groupHeadTemp = await _tempProxy[TemperatureControllerId.GroupHead].GetProcessValueAsync(CancellationToken.None);
            double thermoblockTemp = await _tempProxy[TemperatureControllerId.ThermoBlock].GetProcessValueAsync(CancellationToken.None);
            // For heater enabled, you may need to add a method/property to your controller or proxy
            bool groupHeadHeaterEnabled = false; // Placeholder
            bool thermoblockHeaterEnabled = false; // Placeholder

            // Gather IoBus and analog states
            bool isIoBusConnected = _ioProxy != null; // You may want a more robust check
            double? groupHeadPressure = GetGroupHeadPressure();
            double? pumpSpeed = GetPumpSpeedSetting();

            // Gather valve states
            ValveState groupHeadValve = GetGroupHeadValveState();
            ValveState backflushValve = GetBackFlushValveState();
            ValveState recircValve = GetRecirculationValveState();

            // Gather run/process state
            RunState runState = GetRunState();
            bool isPaused = _pauseCont?.IsPaused ?? false;
            DateTime? runStartTimeUtc = null; // You may want to track this in your run logic
            double? extractionWeight = null; // You may want to track this in your run logic

            return new SystemState(
                groupHeadTemp,
                thermoblockTemp,
                groupHeadHeaterEnabled,
                thermoblockHeaterEnabled,
                isIoBusConnected,
                groupHeadPressure,
                pumpSpeed,
                groupHeadValve,
                backflushValve,
                recircValve,
                runState,
                isPaused,
                runStartTimeUtc,
                extractionWeight
            );
        }

        // Returns raw IO dictionaries from the underlying proxy
        public RawIoState GetRawIoState()
        {
            return new RawIoState(
                IsConnected: _ioProxy.IsCurrentlyConnected,
                DigitalInputs: _ioProxy.DigitalInputState,
                DigitalOutputs: _ioProxy.DigitalOutputState,
                AnalogInputs: _ioProxy.AnalogInputState,
                AnalogOutputs: _ioProxy.AnalogOutputState
            );
        }

        public void NotifyPausedChanged(bool isPaused)
        {
            _stateDeltaSubject.OnNext(new PausedChanged(isPaused));
        }

        /// <summary>
        /// Live genetic auto-tuning using actual IO: pump output as actuator and pressure sensor as feedback.
        /// This method actuates the pump; ensure safe test conditions. Evaluation is serialized and non-parallel.
        /// </summary>
        public class AutotuneSample
        {
            public double TimeSeconds { get; set; }
            public double Setpoint { get; set; }
            public double Process { get; set; }
            public double Output { get; set; }
            public double Proportional { get; set; }
            public double Integral { get; set; }
            public double Derivative { get; set; }
            public bool Windup { get; set; }
            public bool ClampedUpper { get; set; }
            public bool ClampedLower { get; set; }
        }

        public class AutotuneResult
        {
            public double Kp { get; set; }
            public double Ki { get; set; }
            public double Kd { get; set; }
            public double Iae { get; set; }
            public double MaxOvershoot { get; set; }
            public double SteadyStateError { get; set; }
            public double FinalSteadyStateError { get; set; }
            public IReadOnlyList<AutotuneSample> Samples { get; set; } = Array.Empty<AutotuneSample>();
        }

        public async Task<AutotuneResult> AutoTunePidGeneticLiveAsync(
            CancellationToken ct,
            GeneticTunerOptions options,
            double dt,
            int steps,
            double maxSafePressurePsi,
            double targetPressurePsi,
            double maxPumpSpeed,
            double minPumpSpeed)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            await _systemLock.WaitAsync(ct);

            _logger.LogInformation($"[Autotune] Starting live GA autotune: dt={dt}s, steps={steps}, targetPsi={targetPressurePsi}, maxSafePsi={maxSafePressurePsi}, pump=[{minPumpSpeed},{maxPumpSpeed}]");

            // Temporarily apply conservative dynamics to the PID to avoid aggressive steps during tuning
            double prevMaxOutputRate = _pid.MaxOutputRate;
            double prevSetpointRampRate = _pid.SetpointRampRate;
            double prevOutputFilterTau = _pid.OutputFilterTimeConstant;

            try
            {
                // Ensure a safe fluid path so the pump does not deadhead (and spike pressure)

                await SetBackFlushValveStateClosedInternalAsync(ct);
                // Tune pressure at grouphead under flow: open grouphead, close recirculation
                await SetGroupHeadValveStateClosedInternalAsync(ct);
                await SetRecirculationValveStateOpenInternalAsync(ct);

                //set initial pump speed to avoid weird initial conditions
                await ApplyPumpSpeedInternalAsync(minPumpSpeed + 0.5, ct);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                var setpointProfile = BuildAutoTuneSetpointProfile(steps);

                //integral absolute error
                double iaeWeight = 1.0;
                double overshootWeight = 200.0;
                double steadyStateWeight = 300.0;
                double finalErrorWeight = 400.0;
                //get 5 percent of steps, at least 1
                int steadyStateSamples = Math.Max(1, (int)(0.05 * steps));

                int evalCounter = 0;
                
                // Configure controller to align with loop dt and sampling assumptions
                _pid.NominalSamplePeriod = dt;
                _pid.InputFilterTimeConstant = Math.Max(0.0, 0.02); // modest anti-aliasing (20 ms)

                // Ramp output across the allowed band in ~2-3 seconds; ramp setpoint similarly
                _pid.MaxOutputRate = (maxPumpSpeed - minPumpSpeed) / 2.5; // units per second
                _pid.SetpointRampRate = targetPressurePsi / 2.5; // psi per second
                _pid.OutputFilterTimeConstant = 0.2;
                // Derivative filter: prefer time-constant ~ Ts to 2*Ts
                _pid.DerivativeFilterTimeConstant = Math.Max(dt, 2 * dt);
                PidEvalDelegate liveEvaluator = async (parameters, evalCt) =>
                {
                    double prevUpper = _pid.OutputUpperLimit;
                    double prevLower = _pid.OutputLowerLimit;
                    try
                    {
                        // Apply candidate gains and reset controller state
                        _pid.SetGains(parameters.Kp, parameters.Ki, parameters.Kd);
                        _pid.ResetController();
                        // Temporarily sync PID output limits to requested pump speed range
                        _pid.SetOutputLimits(maxPumpSpeed, minPumpSpeed);

                        // Start each evaluation at the minimum pump to avoid pressure spikes

                        await ApplyPumpSpeedInternalAsync(minPumpSpeed + 0.5, evalCt);
                        await Task.Delay(TimeSpan.FromSeconds(1), evalCt);


                        double errorSum = 0;
                        double maxOvershoot = 0;
                        double steadyStateError = 0;
                        double finalSteadyStateError = 0;
                        double prevSp = double.NaN;
                        int stepDir = 0;
                        double[] errors = new double[steps];

                        long ticksPerSecond = System.Diagnostics.Stopwatch.Frequency;
                        long targetTicks = (long)(dt * ticksPerSecond);
                        long prevTicks = System.Diagnostics.Stopwatch.GetTimestamp() - targetTicks;

                        for (int i = 0; i < steps; i++)
                        {
                            if (evalCt.IsCancellationRequested)
                            {
                                _logger.LogWarning($"Eval was cancelled");
                                return new PidResult { Cost = double.MaxValue };
                            }

                            double sp = setpointProfile[i] * targetPressurePsi;
                            double? p = GetGroupHeadPressure();
                            if (!p.HasValue)
                            {
                                _logger.LogWarning($"Unable to get grouphead pressure");
                                return new PidResult { Cost = double.MaxValue };
                            }

                            if (p.Value > maxSafePressurePsi)
                            {
                                _logger.LogWarning($"[Autotune] Safety stop: pressure {p.Value}psi exceeded maxSafe {maxSafePressurePsi}psi during eval");
                                await StopPumpInternalAsync(evalCt);
                                return new PidResult { Cost = double.MaxValue };
                            }

                            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                            long dtTicks = nowTicks - prevTicks;
                            double dtSeconds = dtTicks > 0 ? (double)dtTicks / ticksPerSecond : dt;
                            double u = _pid.PID_iterate(sp, p.Value, TimeSpan.FromSeconds(dtSeconds));
                            prevTicks = nowTicks;
                            //not using _pid.OutputUpperLimit here because override during tuning
                            double clampedU = Math.Max(minPumpSpeed, Math.Min(maxPumpSpeed, u));
                            await ApplyPumpSpeedInternalAsync(clampedU, evalCt);

                            // wait for next sample
                            long loopEndTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                            long workTicks = loopEndTicks - nowTicks;
                            long remainingTicks = targetTicks - workTicks;
                            if (remainingTicks > 0)
                            {
                                int remainingMs = (int)Math.Max(0, (remainingTicks * 1000) / ticksPerSecond);
                                if (remainingMs > 0)
                                    await Task.Delay(remainingMs, evalCt);
                            }

                            p = GetGroupHeadPressure();
                            if (!p.HasValue)
                            {
                                _logger.LogWarning($"Unable to get grouphead pressure");
                                return new PidResult { Cost = double.MaxValue };
                            }

                            double e = Math.Abs(sp - p.Value);
                            errors[i] = e;
                            errorSum += e * dt;

                            if (!double.IsNaN(prevSp) && Math.Abs(sp - prevSp) > 1e-9)
                                stepDir = sp > prevSp ? 1 : -1;
                            double overshoot = 0.0;
                            if (stepDir > 0 && p.Value > sp) overshoot = p.Value - sp;
                            else if (stepDir < 0 && p.Value < sp) overshoot = sp - p.Value;
                            if (overshoot > maxOvershoot) maxOvershoot = overshoot;
                            prevSp = sp;
                        }

                        for (int i = steps - steadyStateSamples; i < steps; i++)
                            steadyStateError += errors[i];
                        steadyStateError /= steadyStateSamples;
                        int tail = Math.Min(10, steps);
                        for (int i = steps - tail; i < steps; i++)
                            finalSteadyStateError += errors[i];
                        finalSteadyStateError /= tail;

                        double cost = iaeWeight * errorSum
                                      + overshootWeight * maxOvershoot
                                      + steadyStateWeight * steadyStateError
                                      + finalErrorWeight * finalSteadyStateError;
                        int evalId = Interlocked.Increment(ref evalCounter);
                        _logger.LogInformation($"[Autotune] Eval {evalId}: IAE={errorSum:F4}*{iaeWeight} + Overshoot={maxOvershoot:F4}*{overshootWeight} + Steady={steadyStateError:F4}*{steadyStateWeight} + Final={finalSteadyStateError:F4}*{finalErrorWeight} => Cost={cost:F6}");



                        return new PidResult { Cost = cost };
                    }
                    catch(Exception e)
                    {
                        _logger.LogWarning($"Failed during eval. Exception: {e.Message}");
                        return new PidResult { Cost = double.MaxValue };
                        throw;
                    }
                    finally
                    {
                        _pid.SetOutputLimits(prevUpper, prevLower);
                    }
                };

                var geneticTuner = new GeneticPidTuner(seed: 42);
                var baseParams = new PidParameters
                {
                    Setpoint = targetPressurePsi,
                    SimulationTime = steps * dt,
                    TimeStep = dt,
                    InitialProcessValue = GetGroupHeadPressure() ?? 0.0
                };

                if (options.EvaluateInParallel)
                {
                    _logger.LogWarning("[Autotune] Parallel GA evaluation is not supported for live tuning; forcing sequential.");
                    options.EvaluateInParallel = false;
                }
                options.OnGenerationEvaluated = (gen, best) =>
                {
                    _logger.LogInformation($"[Autotune] Gen {gen}: best Cost={best.Cost:F6}, Kp={best.Kp:F4}, Ki={best.Ki:F4}, Kd={best.Kd:F4}");
                };

                (double Kp, double Ki, double Kd) best;
                try
                {
                    _logger.LogInformation($"[Autotune] GA tuning started: gen={options.Generations}, pop={options.PopulationSize}");
                    best = await geneticTuner.TuneAsync(liveEvaluator, baseParams, options, ct);
                    _logger.LogInformation($"[Autotune] GA tuning finished. Best gains: Kp={best.Kp:F4}, Ki={best.Ki:F4}, Kd={best.Kd:F4}");
                }
                finally
                {
                    try { await StopPumpInternalAsync(ct); } catch { }
                }

                // Apply tuned gains
                _pid.SetGains(best.Kp, best.Ki, best.Kd);
                _stateDeltaSubject.OnNext(new PidParametersChanged(
                    best.Kp, best.Ki, best.Kd, _pid.N, _pid.OutputUpperLimit, _pid.OutputLowerLimit));

                // Post-tune validation pass collecting samples/metrics
                var samples = new List<AutotuneSample>(steps);
                double iae = 0, maxOvershoot = 0, steadyStateError = 0, finalSteadyStateError = 0;
                int steadyStateSamplesCount = Math.Max(1, (int)(0.05 * steps));
                double prevSp = double.NaN; int stepDir = 0;

                try
                {
                    DateTime prevTime = DateTime.UtcNow;
                    for (int i = 0; i < steps; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        double sp = setpointProfile[i] * targetPressurePsi;
                        double? p = GetGroupHeadPressure();
                        if (!p.HasValue) break;
                        if (p.Value > maxSafePressurePsi)
                        {
                            try { await StopPumpInternalAsync(ct); } catch { }
                            break;
                        }
                        var now = DateTime.UtcNow;
                        double u = _pid.PID_iterate(sp, p.Value, now - prevTime);
                        prevTime = now;
                        double clampedU = Math.Max(minPumpSpeed, Math.Min(maxPumpSpeed, u));
                        await ApplyPumpSpeedInternalAsync(clampedU, ct);

                        try { await Task.Delay(TimeSpan.FromSeconds(dt), ct); } catch { break; }

                        var p2 = GetGroupHeadPressure();
                        double pv = p2 ?? p.Value;
                        double errAbs = Math.Abs(sp - pv);
                        iae += errAbs * dt;
                        if (!double.IsNaN(prevSp) && Math.Abs(sp - prevSp) > 1e-9) stepDir = sp > prevSp ? 1 : -1;
                        double overshoot = 0.0;
                        if (stepDir > 0 && pv > sp) overshoot = pv - sp;
                        else if (stepDir < 0 && pv < sp) overshoot = sp - pv;
                        if (overshoot > maxOvershoot) maxOvershoot = overshoot;
                        prevSp = sp;

                        if (i >= steps - steadyStateSamplesCount) steadyStateError += errAbs;
                        if (i >= steps - 10) finalSteadyStateError += errAbs;

                        samples.Add(new AutotuneSample
                        {
                            TimeSeconds = (i + 1) * dt,
                            Setpoint = sp,
                            Process = pv,
                            Output = clampedU,
                            Proportional = best.Kp * (sp - pv),
                            Integral = best.Ki * _pid.LastIntegral,
                            Derivative = best.Kd * _pid.LastDerivative,
                            Windup = _pid.WindupAlarm,
                            ClampedUpper = Math.Abs(clampedU - _pid.OutputUpperLimit) < 1e-6,
                            ClampedLower = Math.Abs(clampedU - _pid.OutputLowerLimit) < 1e-6
                        });
                    }
                }
                finally
                {
                    try { await StopPumpInternalAsync(ct); } catch { }
                }

                var result = new AutotuneResult
                {
                    Kp = best.Kp,
                    Ki = best.Ki,
                    Kd = best.Kd,
                    Iae = iae,
                    MaxOvershoot = maxOvershoot,
                    SteadyStateError = samples.Count > 0 ? steadyStateError / Math.Max(1, Math.Min(steadyStateSamplesCount, samples.Count)) : 0,
                    FinalSteadyStateError = samples.Count > 0 ? finalSteadyStateError / Math.Min(10, samples.Count) : 0,
                    Samples = samples
                };

                _logger.LogInformation($"[Autotune] Validation: IAE={result.Iae:F4}, MaxOvershoot={result.MaxOvershoot:F4}, SteadyStateError={result.SteadyStateError:F4}, FinalError={result.FinalSteadyStateError:F4}, Samples={result.Samples.Count}");

                return result;
            }
            finally
            {
                // Restore PID dynamics settings
                try
                {
                    _pid.MaxOutputRate = prevMaxOutputRate;
                    _pid.SetpointRampRate = prevSetpointRampRate;
                    _pid.OutputFilterTimeConstant = prevOutputFilterTau;
                }
                catch { }

                try { await StopPumpInternalAsync(ct); } catch { }
                try { await SetAllIdleInternalAsync(ct); } catch { }
                _systemLock.Release();
                _logger.LogInformation("[Autotune] Completed live autotune session");
            }
        }

        //private static double[] BuildSimulatedAutoTuneSetpointProfile(double dt, int steps)
        //{
        //    var profile = new double[steps];
        //    for (int i = 0; i < steps; i++)
        //    {
        //        double t = i * dt;
        //        if (t < 5.0) profile[i] = 0.0;
        //        else if (t < 15.0) profile[i] = 0.95;
        //        else if (t < 25.0) profile[i] = 0.5;
        //        else if (t < 35.0) profile[i] = 0.75;
        //        else if (t < 45.0) profile[i] = 0.25;
        //        else if (t < 55.0) profile[i] = 0.0;
        //        else if (t < 65.0) profile[i] = 0.9;
        //        else if (t < 75.0) profile[i] = 0.3;
        //        else profile[i] = 0.7;
        //    }
        //    return profile;
        //}

        private static double[] BuildAutoTuneSetpointProfile(int steps)
        {
            var profile = new double[steps];
            int half = steps / 2;

            // Simple step profile: first half low, second half high
            for (int i = 0; i < steps; i++)
                profile[i] = i < half ? 0.5 : 1;

            return profile;
        }

        // Wrapper to set temperature and emit state changes
        private async Task SetTemperatureSynchronouslyAsync(
            TemperatureControllerId controllerId,
            int setpoint,
            TimeSpan timeout,
            double tolerance,
            CancellationToken ct)
        {
            var startTime = DateTime.UtcNow;

            try
            {

                await _tempProxy[controllerId].SetSetPointAsync(setpoint, ct);
                await _tempProxy[controllerId].EnableControlLoopAsync(ct);

                _stateDeltaSubject.OnNext(new TemperatureSetpointChanged(controllerId, setpoint));

                while (true)
                {
                    if (DateTime.UtcNow - startTime > timeout)
                        throw new TimeoutException($"Failed to get to specified temperature: {setpoint} after specified seconds: {timeout.TotalSeconds}");

                    var procValue = await _tempProxy[controllerId].GetProcessValueAsync(ct);

                    _stateDeltaSubject.OnNext(new TemperatureChanged(controllerId, procValue));

                    if (Math.Abs(setpoint - procValue) < tolerance)
                        break;

                    await Task.Delay(100, ct);
                }
            }
            catch (Exception)
            {
                await _tempProxy[controllerId].DisableControlLoopAsync(ct);
                throw;
            }

        }

        #region IDisposable

        protected void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                try { _ctSrc?.Cancel(); } catch (Exception) { }
                try { _ioProxy?.Dispose(); } catch (Exception) { }
                try { _tempProxy[TemperatureControllerId.ThermoBlock]?.Dispose(); } catch (Exception) { }
                try { _tempProxy[TemperatureControllerId.GroupHead]?.Dispose(); } catch (Exception) { }
                try { _ctSrc?.Dispose(); } catch (Exception) { }
                try { _runLock?.Dispose(); } catch (Exception) { }
                try { _runScanLock?.Dispose(); } catch (Exception) { }
                try { _systemLock?.Dispose(); } catch (Exception) { }
                try { _connectionSub?.Dispose(); } catch (Exception) { }
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

    public class SystemProxyConfiguration
    {
        // IO mapping
        public ushort RecircValveIO { get; }
        public ushort GroupheadValveIO { get; }
        public ushort BackflushValveIO { get; }
        public ushort RunStatusOutputIO { get; }
        public ushort RunStatusInputIO { get; }
        public ushort PumpSpeedIO { get; }
        public ushort PressureIO { get; }

        // Timing and control
        public int RecircValveOpenDelayMs { get; }
        public int InitialPumpSpeedDelayMs { get; }
        public int TempSettleTolerance { get; }
        public int TempSettleTimeoutSec { get; }
        public int PidLoopDelayMs { get; }
        public int MainScanLoopDelayMs { get; }
        public int RunStateMonitorDelayMs { get; }
        public double PumpStopValue { get; }
        public int SetAllIdleRecircOpenDelayMs { get; }

        // Pressure measurement (4–20 mA → Bar → selected unit)
        public PressureUnit PressureUnit { get; }
        public double SensorMinPressurePsi { get; }
        public double SensorMaxPressurePsi { get; }
        public double SensorMinCurrentmA { get; }
        public double SensorMaxCurrentmA { get; }

        public SystemProxyConfiguration(
            ushort recircValveIO,
            ushort groupheadValveIO,
            ushort backflushValveIO,
            ushort runStatusOutputIO,
            ushort runStatusInputIO,
            ushort pumpSpeedIO,
            ushort pressureIO,
            int recircValveOpenDelayMs,
            int initialPumpSpeedDelayMs,
            int tempSettleTolerance,
            int tempSettleTimeoutSec,
            int pidLoopDelayMs,
            int mainScanLoopDelayMs,
            int runStateMonitorDelayMs,
            double pumpStopValue,
            int setAllIdleRecircOpenDelayMs,
            PressureUnit pressureUnit,
            double sensorMinPressurePsi,
            double sensorMaxPressurePsi,
            double sensorMinCurrentmA,
            double sensorMaxCurrentmA)
        {
            RecircValveIO = recircValveIO;
            GroupheadValveIO = groupheadValveIO;
            BackflushValveIO = backflushValveIO;
            RunStatusOutputIO = runStatusOutputIO;
            RunStatusInputIO = runStatusInputIO;
            PumpSpeedIO = pumpSpeedIO;
            PressureIO = pressureIO;
            RecircValveOpenDelayMs = recircValveOpenDelayMs;
            InitialPumpSpeedDelayMs = initialPumpSpeedDelayMs;
            TempSettleTolerance = tempSettleTolerance;
            TempSettleTimeoutSec = tempSettleTimeoutSec;
            PidLoopDelayMs = pidLoopDelayMs;
            MainScanLoopDelayMs = mainScanLoopDelayMs;
            RunStateMonitorDelayMs = runStateMonitorDelayMs;
            PumpStopValue = pumpStopValue;
            SetAllIdleRecircOpenDelayMs = setAllIdleRecircOpenDelayMs;
            PressureUnit = pressureUnit;
            SensorMinPressurePsi = sensorMinPressurePsi;
            SensorMaxPressurePsi = sensorMaxPressurePsi;
            SensorMinCurrentmA = sensorMinCurrentmA;
            SensorMaxCurrentmA = sensorMaxCurrentmA;
        }
    }

    public record RawIoState(
        bool IsConnected,
        IReadOnlyDictionary<ushort, DigitalIoState?> DigitalInputs,
        IReadOnlyDictionary<ushort, DigitalIoState?> DigitalOutputs,
        IReadOnlyDictionary<ushort, AnalogIoState?> AnalogInputs,
        IReadOnlyDictionary<ushort, AnalogIoState?> AnalogOutputs
    );
}
