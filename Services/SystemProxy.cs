using Microsoft.AspNetCore.SignalR;
using Puck.Services;
using System.Runtime.CompilerServices;
using System.Text;

namespace puck.Services
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

    public struct RunEvent
    {
        public RunEvent(
            string message,
            EventStatusLevel level)
        {
            Message = message;
            Level = level;
        }


        public string Message { get; }
        public EventStatusLevel Level { get; }
    }

    public struct RunResult
    {
        public string RunId { get; set; }
        public RunParameters? RunParameters { get; set; }

        public DateTime? StartTimeUTC { get; set; }
        public DateTime? EndTimeUTC { get; set; }

        public List<RunEvent>? Events { get; set; }
        public RunCompletionStatus CompletionStatus { get; set; }


        public void Clear()
        {
            RunId = string.Empty;
            RunParameters = null;
            StartTimeUTC = null;
            EndTimeUTC = null;
            Events?.Clear();
            Events = null;
            CompletionStatus = RunCompletionStatus.NONE;
        }
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

    public class SystemProxy : IDisposable
    {
        private readonly ILogger<SystemService> _logger;
        private readonly PhoenixProxy _ioProxy;
        private readonly IReadOnlyDictionary<TemperatureControllerId, TemperatureControllerProxy> _tempProxy;
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


        #region IO_MAPPING

        private readonly ushort _recircValve_IO = 1;
        private readonly ushort _groupheadValve_IO = 2;
        private readonly ushort _backflushValve_IO = 3;
        private readonly ushort _runStatusOutput_IO = 4;
        private readonly ushort _runStatusInput_IO = 1;
        private readonly ushort _pumpSpeed_IO = 1;
        private readonly ushort _pressure_IO = 1;

        #endregion

        public SystemProxy(
            ILogger<SystemService> logger,
            PhoenixProxy ioProxy,
            TemperatureControllerContainer tempControllers,
            PauseContainer pauseCont,
            PID pid,
            RunParametersRepo paramRepo,
            RunResultRepo runRepo)
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
        }

        public async Task StartRunScan(CancellationToken ct)
        {
            _logger.LogInformation("Started run scan");

            if (!await _runScanLock.WaitAsync(0))
                throw new Exception("Cannot start run scan if already started");

            var combineCtSrc = CancellationTokenSource.CreateLinkedTokenSource(ct, _ctSrc.Token);

            CancellationTokenSource runStopSrc = new CancellationTokenSource();

            var scanTask = Task.Run(async () =>
            {
                _logger.LogInformation("Entered run scan task");

                RunResult currentRes;

                while (!combineCtSrc.IsCancellationRequested)
                {
                    try
                    {
                        await _pauseCont.WaitIfPausedAsync(ct);

                        if (GetRunState() == RunState.Run)
                        {
                            var starttime = DateTime.UtcNow;
                            var runParams = _paramRepo.GetActiveParameters();

                            currentRes = new RunResult();
                            currentRes.Events = new List<RunEvent>();
                            currentRes.CompletionStatus = RunCompletionStatus.NONE;
                            currentRes.StartTimeUTC = starttime;
                            currentRes.RunId =
                                $"RUN_{starttime.Month}_{starttime.Day}_{starttime.Year}_{starttime.Hour}_{starttime.Minute}_{starttime.Second}_{starttime.Ticks}";

                            currentRes.RunParameters = runParams;

                            _runRepo.SetLatestRunResult(currentRes);

                            await _runLock.WaitAsync(combineCtSrc.Token);
                            await _systemLock.WaitAsync(combineCtSrc.Token);

                            var allCtSrc =
                                CancellationTokenSource
                                .CreateLinkedTokenSource(runStopSrc.Token, combineCtSrc.Token);

                            try
                            {
                                currentRes.Events.Add(new RunEvent("Run starting", EventStatusLevel.INFORMATION));
                                _logger.LogInformation("Run starting");

                                //ESPRESSO CONTROL LOGIC SCAN HERE PASS RUNSTOP TOKEN TO ALL HERE


                                //CLOSE BACKFLUSH VALVE
                                _logger.LogInformation("Closing backflush valve");
                                currentRes.Events.Add(new RunEvent("Closing backflush valve", EventStatusLevel.INFORMATION));
                                await SetBackFlushValveStateClosedAsync(allCtSrc.Token);

                                //CLOSE GROUPHEAD VALVE
                                _logger.LogInformation("Closing grouphead valve");
                                currentRes.Events.Add(new RunEvent("Closing grouphead valve", EventStatusLevel.INFORMATION));
                                await SetGroupHeadValveStateClosedInternalAsync(allCtSrc.Token);

                                //OPEN RECIRC
                                _logger.LogInformation("Opening recirc valve");
                                currentRes.Events.Add(new RunEvent("Opening recirc valve", EventStatusLevel.INFORMATION));
                                await SetRecirculationValveStateOpenInternalAsync(allCtSrc.Token);
                                await Task.Delay(100, allCtSrc.Token);

                                //SET INITIAL PUMP SPEED
                                _logger.LogInformation($"Setting initial pump speed to: {runParams.InitialPumpSpeed}");
                                currentRes.Events.Add(new RunEvent($"Setting initial pump speed to: {runParams.InitialPumpSpeed}", EventStatusLevel.INFORMATION));
                                await ApplyPumpSpeedInternalAsync(runParams.InitialPumpSpeed, allCtSrc.Token);
                                await Task.Delay(TimeSpan.FromMilliseconds(750), allCtSrc.Token);

                                //SET HEATER ENABLED AND WAIT FOR TEMP
                                _logger.LogInformation($"Setting temp to: {runParams.GroupHeadTemperatureFarenheit}");
                                currentRes.Events.Add(new RunEvent($"Setting temp to: {runParams.GroupHeadTemperatureFarenheit}", EventStatusLevel.INFORMATION));
                                await _tempProxy[TemperatureControllerId.GroupHead].ApplySetPointSynchronouslyAsync(runParams.GroupHeadTemperatureFarenheit, 2, TimeSpan.FromSeconds(30), allCtSrc.Token);

                                _logger.LogInformation($"Setting temp to: {runParams.ThermoblockTemperatureFarenheit}");
                                currentRes.Events.Add(new RunEvent($"Setting temp to: {runParams.ThermoblockTemperatureFarenheit}", EventStatusLevel.INFORMATION));
                                await _tempProxy[TemperatureControllerId.ThermoBlock].ApplySetPointSynchronouslyAsync(runParams.ThermoblockTemperatureFarenheit, 2, TimeSpan.FromSeconds(30), allCtSrc.Token);


                                //TARE SCALE
                                //TODO
                                //OPEN GROUPHEAD VALVE
                                await SetGroupHeadValveStateOpenInternalAsync(allCtSrc.Token);
                                //CLOSE RECIRC 
                                await SetRecirculationValveStateClosedInternalAsync(allCtSrc.Token);
                                //START PID PRESSURE LOOP

                                double weight = 0;
                                DateTime startTime = DateTime.UtcNow;

                                //LOOP UNTIL AT WEIGHT OR TIMEOUT
                                while (weight < runParams.ExtractionWeightGrams)
                                {
                                    //TIMEOUT
                                    if ((DateTime.UtcNow - startTime) > TimeSpan.FromSeconds(runParams.MaxExtractionSeconds))
                                    {
                                        string msg =
                                            $"Timeout occurred while running. Extraction exceeded timeout: {runParams.MaxExtractionSeconds} so exiting extraction routine";

                                        currentRes.Events.Add(new RunEvent(msg, EventStatusLevel.ERROR));

                                        throw new Exception(msg);
                                    }

                                    weight += 10;
                                    var pressure = GetGroupHeadPressure();

                                    if (!pressure.HasValue)
                                    {
                                        string msg =
                                            $"Failed to get grouphead pressure so exiting extraction routine";

                                        currentRes.Events.Add(new RunEvent(msg, EventStatusLevel.ERROR));
                                        throw new Exception(msg);
                                    }

                                    var groupTemp = _tempProxy[TemperatureControllerId.GroupHead].GetProcessValue();
                                    var thermoblockTemp = _tempProxy[TemperatureControllerId.ThermoBlock].GetProcessValue();

                                    await Task.Delay(TimeSpan.FromMilliseconds(500), allCtSrc.Token);
                                    var output = _pid.PID_iterate(runParams.TargetPressureBar, pressure.Value, DateTime.UtcNow - startTime);
                                    await ApplyPumpSpeedInternalAsync(output, allCtSrc.Token);

                                    var loopEvent =
                                        new RunEvent(
                                            $"Loop event in extraction routine. Pump speed: {output}." +
                                            $"Pressure: {pressure}. Group temp: {groupTemp}. ThermoblockTemp: {thermoblockTemp}",
                                             EventStatusLevel.INFORMATION);

                                    currentRes.Events.Add(loopEvent);
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
                                //CLEAN UP STEPS
                                try { await StopPumpAsync(allCtSrc.Token); } catch { }
                                try { await _tempProxy[TemperatureControllerId.ThermoBlock].DisableControlLoopAsync(allCtSrc.Token); } catch { }
                                try { await _tempProxy[TemperatureControllerId.GroupHead].DisableControlLoopAsync(allCtSrc.Token); } catch { }
                                try { await SetGroupHeadValveStateClosedInternalAsync(allCtSrc.Token); } catch { }
                                try { await SetRunStatusIdleAsync(allCtSrc.Token); } catch { }
                                try { await SetAllIdleInternalAsync(allCtSrc.Token); } catch { }

                                var endtime = DateTime.UtcNow;

                                currentRes.Events.Add(
                                    new RunEvent(
                                        $"Completed extraction run with id: {currentRes.RunId} and completion status: {currentRes.CompletionStatus}", 
                                        EventStatusLevel.INFORMATION));
                                        
                                _runRepo.Commit();

                                _runLock.Release();
                                _systemLock.Release();
                                allCtSrc.Dispose();
                                _logger.LogInformation("Run finished");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, $"Failed in {nameof(StartRunScan)} within {nameof(SystemProxy)}: {e.Message}");
                    }
                    finally
                    {
                        await Task.Delay(250, combineCtSrc.Token);
                    }
                }
            });


            var runStateScanTask = Task.Run(async () =>
            {
                try
                {
                    //ESPRESSO CONTROL LOGIC SCAN HERE
                    while (!combineCtSrc.IsCancellationRequested)
                    {
                        try
                        {
                            if (!await _runLock.WaitAsync(0) && GetRunState() == RunState.Idle)
                            {
                                _logger.LogInformation("Cancelling mid-process run");

                                //cancel run process and wait for it to release run lock
                                runStopSrc.Cancel();
                                runStopSrc.Dispose();
                                runStopSrc = new CancellationTokenSource();

                                await _runLock.WaitAsync(combineCtSrc.Token);
                                _runLock.Release();
                            }
                            else
                            {
                                _runLock.Release();
                            }
                        }
                        catch (Exception e)
                        {
                            _logger.LogError(e, $"Failed in {nameof(StartRunScan)} within {nameof(SystemProxy)}: {e.Message}");
                        }
                        finally
                        {
                            await Task.Delay(250, combineCtSrc.Token);
                        }
                    }
                }
                finally
                {
                    runStopSrc.Cancel();

                    try
                    {
                        await scanTask;
                    }
                    finally
                    {
                        runStopSrc.Dispose();
                    }
                }
            });

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
            if (!_ioProxy.DigitalInputState.TryGetValue(_runStatusInput_IO, out var val))
                throw new Exception($"Error in {nameof(GetRunState)} within {nameof(SystemProxy)}. No digital input found with index 1");

            if (!val.HasValue)
                return RunState.None;

            var runState = val.Value.State ? RunState.Run : RunState.Idle;

            return runState;
        }


        public ValveState GetRecirculationValveState()
        {
            var state = GetValveState(_recircValve_IO);
            return state;
        }

        public ValveState GetGroupHeadValveState()
        {
            var state = GetValveState(_groupheadValve_IO);
            return state;
        }

        public ValveState GetBackFlushValveState()
        {
            var state = GetValveState(_backflushValve_IO);
            return state;
        }


        public double? GetPumpSpeedSetting()
        {
            var pumpSpeed = GetAnalogOutputState(_pumpSpeed_IO);

            return pumpSpeed.HasValue ? pumpSpeed.Value.State : null;
        }

        public double? GetGroupHeadPressure()
        {
            var press = GetAnalogInputState(_pressure_IO);

            //TODO: ADD BARR CONVERSION

            return press.HasValue ? press.Value.State : null;
        }


        public double? GetProcessTemperature(TemperatureControllerId controllerId)
        {
            var temp = _tempProxy[controllerId].GetProcessValue();

            return temp;
        }

        public double? GetSetPointTemperature(TemperatureControllerId controllerId)
        {
            var temp = _tempProxy[controllerId].GetSetValue();

            return temp;
        }

        private async Task ExecuteSystemActionAsync(
            Func<Task> action,
            CancellationToken ct)
        {
            if (!_runLock.Wait(0))
                throw new Exception("Cannot execute operation while run is in process");

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

        private Task RunInternalAsync(
            RunParameters runParams,
            CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_runStatusOutput_IO, true, ct);
        }

        public Task RunAsync(
            RunParameters runParams,
            CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => RunInternalAsync(runParams, ct), ct);
        }

        public Task SetRunStatusIdleAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_runStatusOutput_IO, false, ct);
        }

        private Task SetTemperatureSetpointInternalAsync(int setpoint, TemperatureControllerId controllerId, CancellationToken ct)
        {
            return _tempProxy[controllerId].SetSetPointAsync(setpoint, ct);
        }

        public Task SetTemperatureSetpointAsync(int setpoint, CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetTemperatureSetpointAsync(setpoint, ct), ct);
        }

        private Task ApplyPumpSpeedInternalAsync(double speed, CancellationToken ct)
        {
            return _ioProxy.SetAnalogOutputStateAsync(_pumpSpeed_IO, speed, ct);
        }

        public Task ApplyPumpSpeedAsync(double speed, CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => ApplyPumpSpeedInternalAsync(speed, ct), ct);
        }

        private Task StopPumpInternalAsync(CancellationToken ct)
        {
            return _ioProxy.SetAnalogOutputStateAsync(_pumpSpeed_IO, 4, ct);
        }

        public Task StopPumpAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => StopPumpInternalAsync(ct), ct);
        }


        //GROUPHEAD
        private Task SetGroupHeadValveStateOpenInternalAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_groupheadValve_IO, true, ct);
        }

        public Task SetGroupHeadValveStateOpenAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetGroupHeadValveStateOpenInternalAsync(ct), ct);
        }

        private Task SetGroupHeadValveStateClosedInternalAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_groupheadValve_IO, false, ct);
        }

        public Task SetGroupHeadValveStateClosedAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetGroupHeadValveStateClosedInternalAsync(ct), ct);
        }

        //BACKFLUSH
        private Task SetBackFlushValveStateOpenInternalAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_backflushValve_IO, true, ct);
        }

        public Task SetBackFlushValveStateOpenAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetBackFlushValveStateOpenInternalAsync(ct), ct);
        }

        private Task SetBackFlushValveStateClosedInternalAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_backflushValve_IO, false, ct);
        }

        public Task SetBackFlushValveStateClosedAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetBackFlushValveStateClosedInternalAsync(ct), ct);
        }


        //RECIRC
        private Task SetRecirculationValveStateOpenInternalAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_recircValve_IO, true, ct);
        }

        public Task SetRecirculationValveStateOpenAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetRecirculationValveStateOpenInternalAsync(ct), ct);
        }

        private Task SetRecirculationValveStateClosedInternalAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_recircValve_IO, false, ct);
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
            await Task.Delay(250, ct);
            await SetGroupHeadValveStateClosedInternalAsync(ct);
            await SetRecirculationValveStateClosedInternalAsync(ct);
            await SetBackFlushValveStateClosedInternalAsync(ct);
        }

        public Task SetAllIdleAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetAllIdleInternalAsync(ct), ct);
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
