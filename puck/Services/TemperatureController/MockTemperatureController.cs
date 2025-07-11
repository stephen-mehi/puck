using System;
using System.Threading;
using System.Threading.Tasks;

namespace Puck.Services.TemperatureController
{
    /// <summary>
    /// Mock for ITemperatureController, simulates state and allows configuration for testing.
    /// </summary>
    public class MockTemperatureController : ITemperatureController
    {
        private int _setValue = 100;
        private int _processValue = 100;
        private bool _controlLoopActive = false;
        private bool _disposed = false;
        private TimeSpan _responseDelay = TimeSpan.FromMilliseconds(50);

        // Test hook for simulating a stuck process value
        public bool SimulateStuckProcessValue { get; set; } = false;

        // For test configuration
        public void SetProcessValue(int value) => _processValue = value;
        public void SetSetValue(int value) => _setValue = value;
        public void SetResponseDelay(TimeSpan delay) => _responseDelay = delay;
        public void SetControlLoopActive(bool active) => _controlLoopActive = active;

        public async Task SetSetPointAsync(int setpoint, CancellationToken ct = default)
        {
            await Task.Delay(_responseDelay, ct);
            _setValue = setpoint;
            if (!SimulateStuckProcessValue)
                _processValue = setpoint; // Simulate instant process value change
        }

        public async Task ApplySetPointSynchronouslyAsync(int tempSetPoint, double tolerance, TimeSpan timeout, CancellationToken ct = default)
        {
            await SetSetPointAsync(tempSetPoint, ct);
            _controlLoopActive = true;
            var start = DateTime.UtcNow;
            while (Math.Abs(tempSetPoint - _processValue) > tolerance)
            {
                if (DateTime.UtcNow - start > timeout)
                    throw new TimeoutException();
                await Task.Delay(10, ct);
            }
        }

        public double? GetSetValue() => _setValue;
        public double? GetProcessValue() => _processValue;

        public Task DisableControlLoopAsync(CancellationToken ct = default)
        {
            _controlLoopActive = false;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}