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

    public class SystemProxy
    {
        private readonly ILogger<SystemService> _logger;
        private readonly PhoenixProxy _ioProxy;
        private readonly TemperatureControllerProxy _tempProxy;
        private readonly CancellationTokenSource _ctSrc;
        private readonly SemaphoreSlim _systemLock;

        private readonly Task _scanTask;

        public SystemProxy(
            ILogger<SystemService> logger,
            PhoenixProxy ioProxy,
            TemperatureControllerProxy tempProxy)
        {
            _logger = logger;
            _ioProxy = ioProxy;
            _tempProxy = tempProxy;
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

        //public Task ApplyPumpSpeedAsync(double speed, CancellationToken ct)
        //{
        //}

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
    }
}
