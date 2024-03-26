using Microsoft.AspNetCore.SignalR;
using Puck.Services;
using System.Runtime.CompilerServices;

namespace puck.Services
{
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

    public class SystemProxy : IDisposable
    {
        private readonly ILogger<SystemService> _logger;
        private readonly PhoenixProxy _ioProxy;
        private readonly TemperatureControllerProxy _tempProxy;
        private readonly CancellationTokenSource _ctSrc;
        private readonly SemaphoreSlim _systemLock;
        private readonly SemaphoreSlim _runLock;
        private readonly SemaphoreSlim _runScanLock;
        private Task? _scanTask;

        private bool _isDisposed;


        #region IO_MAPPING

        private readonly ushort _recircValve_IO = 1;
        private readonly ushort _groupheadValve_IO = 2;
        private readonly ushort _runStatusOutput_IO = 4;
        private readonly ushort _runStatusInput_IO = 1;
        private readonly ushort _pumpSpeed_IO = 1;
        private readonly ushort _pressure_IO = 1;


        #endregion

        public SystemProxy(
            ILogger<SystemService> logger,
            PhoenixProxy ioProxy,
            TemperatureControllerProxy tempProxy)
        {
            _logger = logger;
            _ioProxy = ioProxy;
            _tempProxy = tempProxy;
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
                while (!combineCtSrc.IsCancellationRequested)
                {
                    try
                    {
                        if (GetRunState() == RunState.Run)
                        {
                            _logger.LogInformation("Run starting");

                            await _runLock.WaitAsync(combineCtSrc.Token);
                            await _systemLock.WaitAsync(combineCtSrc.Token);
                            var allCtSrc =
                                CancellationTokenSource
                                .CreateLinkedTokenSource(runStopSrc.Token, combineCtSrc.Token);

                            try
                            {
                                //ESPRESSO CONTROL LOGIC SCAN HERE PASS RUNSTOP TOKEN TO ALL HERE

                                _logger.LogInformation("Closing grouphead valve");
                                //CLOSE GROUPHEAD VALVE
                                await SetGroupHeadValveStateClosedInternalAsync(allCtSrc.Token);
                                _logger.LogInformation("Opening recirc valve");
                                //OPEN RECIRC
                                await SetRecirculationValveStateOpenInternalAsync(allCtSrc.Token);
                                //SET FIXED PUMP SPEED

                                _logger.LogInformation("Setting temp");
                                //SET HEATER ENABLED AND WAIT FOR TEMP
                                await _tempProxy.ApplySetPointSynchronouslyAsync(100, 2, TimeSpan.FromSeconds(30), allCtSrc.Token);

                                //TARE SCALE
                                //OPEN GROUPHEAD VALVE
                                //CLOSE RECIRC 
                                //START PID PRESSURE LOOP
                                //RUN UNTIL SCALE REACHES WEIGHT
                                //STOP PUMP
                                //DISABLE HEATER
                                //CLOSE GROUPHEAD VALVE
                                //OPEN RECIRC
                                //
                            }
                            finally
                            {
                                await SetRunStatusIdleAsync(allCtSrc.Token);
                                await SetAllIdleInternalAsync(allCtSrc.Token);
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
                    await scanTask;
                    runStopSrc.Dispose();
                }
            });

            _scanTask = Task.WhenAll(scanTask, runStateScanTask);
            await _scanTask;
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


        public double? GetPumpSpeedSetting()
        {
            var pumpSpeed = GetAnalogOutputState(_pumpSpeed_IO);

            return pumpSpeed.HasValue ? pumpSpeed.Value.State : null;
        }

        public double? GetGroupHeadPressure()
        {
            var pumpSpeed = GetAnalogInputState(_pressure_IO);

            return pumpSpeed.HasValue ? pumpSpeed.Value.State : null;
        }


        public double? GetProcessTemperature()
        {
            var temp = _tempProxy.GetProcessValue();

            return temp;
        }

        public double? GetSetPointTemperature()
        {
            var temp = _tempProxy.GetSetValue();

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

        private Task SetRunStatusRunInternalAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_runStatusOutput_IO, true, ct);
        }

        public Task SetRunStatusRunAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => SetRunStatusRunInternalAsync(ct), ct);
        }

        public Task SetRunStatusIdleAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(_runStatusOutput_IO, false, ct);
        }

        private Task SetTemperatureSetpointInternalAsync(int setpoint, CancellationToken ct)
        {
            return _tempProxy.SetSetPointAsync(setpoint, ct);
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
            await _tempProxy.DisableControlLoopAsync(ct);
            await StopPumpInternalAsync(ct);
            await SetRecirculationValveStateOpenInternalAsync(ct);
            await Task.Delay(250, ct);
            await SetGroupHeadValveStateClosedInternalAsync(ct);
            await SetRecirculationValveStateClosedInternalAsync(ct);
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
                try { _tempProxy?.Dispose(); } catch (Exception) { }
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
