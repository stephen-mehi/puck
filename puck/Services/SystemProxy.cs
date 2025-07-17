using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using puck.Services.IoBus;
using puck.Services.PID;
using Puck.Services.TemperatureController;
using System.Runtime.CompilerServices;
using System.Text;
using Puck.Models;
using System.Reactive.Subjects;
using System.Reactive.Linq;
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
        private readonly RunParametersRepo _paramRepo;
        private readonly RunResultRepo _runRepo;

        private Task? _scanTask;
        private bool _isDisposed;
        private IDisposable? _connectionSub;

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
        private readonly double _extractionWeightIncrement;
        private readonly int _pidLoopDelayMs;
        private readonly int _mainScanLoopDelayMs;
        private readonly int _runStateMonitorDelayMs;
        private readonly double _pumpStopValue;
        private readonly int _setAllIdleRecircOpenDelayMs;

        private readonly Subject<SystemStateDelta> _stateDeltaSubject = new();
        private readonly BehaviorSubject<ProcessDeviceState> _stateSubject;
        public IObservable<ProcessDeviceState> StateObservable => _stateSubject.AsObservable();

        public SystemProxy(
            ILogger<SystemService> logger,
            IPhoenixProxy ioProxy,
            TemperatureControllerContainer tempControllers,
            PauseContainer pauseCont,
            PID pid,
            RunParametersRepo paramRepo,
            RunResultRepo runRepo,
            // IO mapping
            ushort recircValveIO = 1,
            ushort groupheadValveIO = 2,
            ushort backflushValveIO = 3,
            ushort runStatusOutputIO = 4,
            ushort runStatusInputIO = 1,
            ushort pumpSpeedIO = 1,
            ushort pressureIO = 1,
            // Timing and control
            int recircValveOpenDelayMs = 100,
            int initialPumpSpeedDelayMs = 750,
            int tempSettleTolerance = 2,
            int tempSettleTimeoutSec = 30,
            double extractionWeightIncrement = 10,
            int pidLoopDelayMs = 500,
            int mainScanLoopDelayMs = 250,
            int runStateMonitorDelayMs = 250,
            double pumpStopValue = 4,
            int setAllIdleRecircOpenDelayMs = 250
        )
        {
            _runRepo = runRepo;
            _paramRepo = paramRepo;
            _pid = pid;
            _pauseCont = pauseCont;
            _logger = logger;
            _ioProxy = ioProxy;
            _tempProxy = tempControllers.TemperatureControllers;
            _systemLock = new SemaphoreSlim(1, 1);
            _runLock = new SemaphoreSlim(1, 1);
            _runScanLock = new SemaphoreSlim(1, 1);
            _ctSrc = new CancellationTokenSource();
            // IO mapping
            _recircValveIO = recircValveIO;
            _groupheadValveIO = groupheadValveIO;
            _backflushValveIO = backflushValveIO;
            _runStatusOutputIO = runStatusOutputIO;
            _runStatusInputIO = runStatusInputIO;
            _pumpSpeedIO = pumpSpeedIO;
            _pressureIO = pressureIO;
            // Timing and control
            _recircValveOpenDelayMs = recircValveOpenDelayMs;
            _initialPumpSpeedDelayMs = initialPumpSpeedDelayMs;
            _tempSettleTolerance = tempSettleTolerance;
            _tempSettleTimeoutSec = tempSettleTimeoutSec;
            _extractionWeightIncrement = extractionWeightIncrement;
            _pidLoopDelayMs = pidLoopDelayMs;
            _mainScanLoopDelayMs = mainScanLoopDelayMs;
            _runStateMonitorDelayMs = runStateMonitorDelayMs;
            _pumpStopValue = pumpStopValue;
            _setAllIdleRecircOpenDelayMs = setAllIdleRecircOpenDelayMs;
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
                    TemperatureControllerId.ThermoBlock => oldState with { ThermoblockSetpoint = newSetpoint, StateTimestampUtc = now, GeneralStatusMessage = $"Thermoblock setpoint set to {newSetpoint}" },
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
                            var runParams = _paramRepo.GetActiveParameters();

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
                                DateTime startTime = DateTime.UtcNow;

                                // Extraction loop: runs until target weight or timeout
                                while (weight < runParams.ExtractionWeightGrams)
                                {
                                    // (Consider adding pause check here for better responsiveness)

                                    //Timeout check: abort if extraction takes too long
                                    if ((DateTime.UtcNow - startTime) > TimeSpan.FromSeconds(runParams.MaxExtractionSeconds))
                                    {
                                        string msg =
                                            $"Timeout occurred while running. Extraction exceeded timeout: {runParams.MaxExtractionSeconds} so exiting extraction routine";

                                        _stateDeltaSubject.OnNext(new StatusMessageChanged(msg));
                                        throw new Exception(msg);
                                    }

                                    weight += _extractionWeightIncrement;
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
                                    var output = _pid.PID_iterate(runParams.TargetPressureBar, pressure.Value, DateTime.UtcNow - startTime);
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

        private double MapValueToRange(
            double value,
            double sourceRangeMin,
            double sourceRangeMax,
            double targetRangeMin,
            double targetRangeMax)
        {
            //map using y = mx + B where:
            //m = (targetRangeMax - targetRangeMin)/(sourceRangeMax - sourceRangeMin)
            //B = targetRangeMin
            //x = value - sourceRangeMin 
            //y = output
            var y = (value - sourceRangeMin) * ((targetRangeMax - targetRangeMin) / (sourceRangeMax - sourceRangeMin)) + targetRangeMin;

            return y;
        }

        public RunState GetRunState()
        {
            //TODO: CHANGE THIS BACK TO DIGITAL INPUT ONCE CONNECTED TO HARDWARE
            if (!_ioProxy.DigitalOutputState.TryGetValue(_runStatusOutputIO, out var val))
                throw new Exception($"Error in {nameof(GetRunState)} within {nameof(SystemProxy)}. No digital input found with index {_runStatusInputIO}");

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

            //TODO: ADD BARR CONVERSION

            return press.HasValue ? press.Value.State : null;
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

        public Task RunAsync(
            RunParameters runParams,
            CancellationToken ct)
        {
            _paramRepo.SetActiveParameters(runParams);

            return ExecuteSystemActionAsync(() => RunInternalAsync(ct), ct);
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
            double thermoblockTemp = await  _tempProxy[TemperatureControllerId.ThermoBlock].GetProcessValueAsync(CancellationToken.None);
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

        public void NotifyPausedChanged(bool isPaused)
        {
            _stateDeltaSubject.OnNext(new PausedChanged(isPaused));
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
                try { _scanTask?.Wait(5000); } catch (Exception) { }
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
}
