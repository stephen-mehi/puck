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

        private readonly Task _scanTask;
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
            _ctSrc = new CancellationTokenSource();
            _scanTask = StartRunScan(_ctSrc.Token);
        }

        public Task StartRunScan(CancellationToken ct)
        {
            CancellationTokenSource runStopSrc = new CancellationTokenSource();

            var runStateScanTask = Task.Run(async () =>
            {
                //ESPRESSO CONTROL LOGIC SCAN HERE
                while (!_ctSrc.IsCancellationRequested)
                {
                    try
                    {
                        if(_runLock.CurrentCount > 0 && GetRunState() == RunState.Idle)
                        {
                            //cancel run process and wait for it to release run lock
                            runStopSrc.Cancel();
                            runStopSrc.Dispose();
                            runStopSrc = new CancellationTokenSource();

                            await _runLock.WaitAsync(ct);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError($"Failed in {nameof(StartRunScan)} within {nameof(SystemProxy)}: {e.Message}");
                    }
                    finally
                    {
                        await Task.Delay(10, _ctSrc.Token);
                    }
                }
            });

            var scanTask = Task.Run(async () =>
            {
                while (!_ctSrc.IsCancellationRequested)
                {
                    try
                    {
                        if(GetRunState() == RunState.Run)
                        {
                            await _runLock.WaitAsync(ct);
                            await _systemLock.WaitAsync(ct);

                            try
                            {
                                //ESPRESSO CONTROL LOGIC SCAN HERE PASS RUNSTOP TOKEN TO ALL HERE
                            }
                            finally
                            {
                                _runLock.Release();
                                _systemLock.Release();
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError($"Failed in {nameof(StartRunScan)} within {nameof(SystemProxy)}: {e.Message}");
                    }
                    finally
                    {
                        await Task.Delay(10, _ctSrc.Token);
                    }
                }
            });

            return Task.WhenAll(scanTask, runStateScanTask);
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
            if (!_ioProxy.DigitalInputState.TryGetValue(0, out var val))
                throw new Exception($"Error in {nameof(GetRunState)} within {nameof(SystemProxy)}. No digital input found with index 0");

            if (!val.HasValue)
                return RunState.None;

            var runState = val.Value.State ? RunState.Run : RunState.Idle;

            return runState;
        }


        public ValveState GetRecirculationValveState()
        {
            var state = GetValveState(0);
            return state;
        }

        public ValveState GetGroupHeadValveState()
        {
            var state = GetValveState(1);
            return state;
        }


        public double? GetPumpSpeedSetting()
        {
            var pumpSpeed = GetAnalogInputState(0);

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

        public Task SetTemperatureSetpointAsync(int setpoint, CancellationToken ct)
        {
            return _tempProxy.SetSetPointAsync(setpoint, ct);
        }

        public Task ApplyPumpSpeedAsync(double speed, CancellationToken ct)
        {
            return _ioProxy.SetAnalogOutputStateAsync(0, speed, ct);
        }

        public Task StopPumpAsync(CancellationToken ct)
        {
            return _ioProxy.SetAnalogOutputStateAsync(0, 0, ct);
        }

        public Task SetGroupHeadValveStateOpenAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(1, true, ct);
        }
        public Task SetGroupHeadValveStateClosedAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(1, false, ct);
        }

        public Task SetRecirculationValveStateOpenAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(0, true, ct);
        }

        public Task SetRecirculationValveStateClosedAsync(CancellationToken ct)
        {
            return _ioProxy.SetDigitalOutputStateAsync(0, false, ct);
        }

        public Task SetAllIdleAsync(CancellationToken ct)
        {

        }

        

        #region IDisposable

        protected void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _ctSrc.Cancel();
                _scanTask.Wait(5000);
                _ioProxy?.Dispose();
                _tempProxy?.Dispose();
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
