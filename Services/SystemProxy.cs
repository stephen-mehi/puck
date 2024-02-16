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

            await _runScanLock.WaitAsync(combineCtSrc.Token);

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
                                await Task.Delay(TimeSpan.FromSeconds(30), allCtSrc.Token);
                            }
                            finally
                            {
                                _runLock.Release();
                                _systemLock.Release();
                                allCtSrc.Dispose();
                                _logger.LogInformation("Run finished");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError($"Failed in {nameof(StartRunScan)} within {nameof(SystemProxy)}: {e.Message}");
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
                        }
                        catch (Exception e)
                        {
                            _logger.LogError($"Failed in {nameof(StartRunScan)} within {nameof(SystemProxy)}: {e.Message}");
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
            if (!_ioProxy.DigitalInputState.TryGetValue(1, out var val))
                throw new Exception($"Error in {nameof(GetRunState)} within {nameof(SystemProxy)}. No digital input found with index 1");

            if (!val.HasValue)
                return RunState.None;

            var runState = val.Value.State ? RunState.Run : RunState.Idle;

            return runState;
        }


        public ValveState GetRecirculationValveState()
        {
            var state = GetValveState(1);
            return state;
        }

        public ValveState GetGroupHeadValveState()
        {
            var state = GetValveState(3);
            return state;
        }


        public double? GetPumpSpeedSetting()
        {
            var pumpSpeed = GetAnalogInputState(2);

            return pumpSpeed.HasValue ? pumpSpeed.Value.State : null;
        }

        public double? GetGroupHeadPressure()
        {
            var pumpSpeed = GetAnalogInputState(1);

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
            if (_runLock.CurrentCount == 0)
                throw new Exception("Cannot execute operation while run is in process");

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

        public Task SetRunStatusRun(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => _ioProxy.SetDigitalOutputStateAsync(2, true, ct), ct);
        }

        public Task SetRunStatusIdle(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(2, false, ct);
        }

        public Task SetTemperatureSetpointAsync(int setpoint, CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => _tempProxy.SetSetPointAsync(setpoint, ct), ct);
        }

        public Task ApplyPumpSpeedAsync(double speed, CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => _ioProxy.SetAnalogOutputStateAsync(1, speed, ct), ct);
        }

        public Task StopPumpAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => _ioProxy.SetAnalogOutputStateAsync(1, 0, ct), ct);
        }

        public Task SetGroupHeadValveStateOpenAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => _ioProxy.SetDigitalOutputStateAsync(3, true, ct), ct);
        }

        public Task SetGroupHeadValveStateClosedAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => _ioProxy.SetDigitalOutputStateAsync(3, false, ct), ct);
        }

        public Task SetRecirculationValveStateOpenAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => _ioProxy.SetDigitalOutputStateAsync(1, true, ct), ct);
        }

        public Task SetRecirculationValveStateClosedAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(() => _ioProxy.SetDigitalOutputStateAsync(1, false, ct), ct);
        }

        public Task SetAllIdleAsync(CancellationToken ct)
        {
            return ExecuteSystemActionAsync(async () =>
            {

                await StopPumpAsync(ct);
                await SetRecirculationValveStateOpenAsync(ct);
                await Task.Delay(250, ct);
                await SetGroupHeadValveStateClosedAsync(ct);
                await SetRecirculationValveStateClosedAsync(ct);

            }, ct);
        }



        #region IDisposable

        protected void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _ctSrc.Cancel();
                _scanTask?.Wait(5000);
                _ioProxy?.Dispose();
                _tempProxy?.Dispose();
                _ctSrc?.Dispose();

                _runLock?.Dispose();
                _runScanLock?.Dispose();
                _systemLock?.Dispose();
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
