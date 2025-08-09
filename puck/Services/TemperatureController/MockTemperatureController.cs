using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Puck.Services.TemperatureController
{
    /// <summary>
    /// Mock for ITemperatureController, simulates state and allows configuration for testing.
    /// </summary>
    public class MockTemperatureController : ITemperatureController
    {
        private int _setValue = 100;
        private double _processValue = 100;
        private bool _controlLoopActive = false;
        private bool _disposed = false;
        private TimeSpan _responseDelay = TimeSpan.FromMilliseconds(50);
        private double _approachRate = 0.25; // fraction of difference per step
        private int _approachIntervalMs = 100; // ms between steps
        private CancellationTokenSource? _loopCts;
        private readonly object _lock = new();

        // Test hook for simulating a stuck process value
        public bool SimulateStuckProcessValue { get; set; } = false;

        // For test configuration
        public void SetProcessValue(double value) { lock(_lock) { _processValue = value; } }
        public void SetSetValue(double value) { lock(_lock) { _setValue = (int)value; } }
        public void SetResponseDelay(TimeSpan delay) => _responseDelay = delay;
        public void SetControlLoopActive(bool active) => _controlLoopActive = active;
        public void SetApproachRate(double rate) => _approachRate = rate;
        public void SetApproachIntervalMs(int ms) => _approachIntervalMs = ms;

        public async Task SetSetPointAsync(int setpoint, CancellationToken ct = default)
        {
            await Task.Delay(_responseDelay, ct);
            lock(_lock) { _setValue = setpoint; }
            // Do not instantly change process value; let the control loop handle it
        }

        public async Task ApplySetPointSynchronouslyAsync(int tempSetPoint, double tolerance, TimeSpan timeout, CancellationToken ct = default)
        {
            await SetSetPointAsync(tempSetPoint, ct);
            await EnableControlLoopAsync(ct);
            var start = DateTime.UtcNow;
            while (true)
            {
                double pv;
                lock(_lock) { pv = _processValue; }
                if (Math.Abs(tempSetPoint - pv) <= tolerance)
                    break;
                if (DateTime.UtcNow - start > timeout)
                    throw new TimeoutException();
                await Task.Delay(10, ct);
            }
        }


        public Task DisableControlLoopAsync(CancellationToken ct = default)
        {
            _controlLoopActive = false;
            _loopCts?.Cancel();
            _loopCts = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _disposed = true;
            _loopCts?.Cancel();
        }

        public Task EnableControlLoopAsync(CancellationToken ct = default)
        {
            _controlLoopActive = true;
            if (_loopCts == null || _loopCts.IsCancellationRequested)
            {
                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                Task.Run(async () =>
                {
                    while (!_disposed && _controlLoopActive && !token.IsCancellationRequested)
                    {
                        if (SimulateStuckProcessValue)
                        {
                            await Task.Delay(_approachIntervalMs, token);
                            continue;
                        }
                        double pv, sv;
                        lock(_lock)
                        {
                            pv = _processValue;
                            sv = _setValue;
                        }
                        var diff = sv - pv;
                        if (Math.Abs(diff) > 0.01)
                        {
                            var step = diff * _approachRate;
                            if (Math.Abs(step) < 0.01) step = Math.Sign(diff) * 0.01;
                            lock(_lock) { _processValue += step; }
                        }
                        else
                        {
                            lock(_lock) { _processValue = sv; }
                        }
                        await Task.Delay(_approachIntervalMs, token);
                    }
                }, token);
            }
            return Task.CompletedTask;
        }

        public Task<double> GetSetValueAsync(CancellationToken ct = default)
        {
            lock(_lock) { return Task.FromResult((double)_setValue); }
        }

        public Task<double> GetProcessValueAsync(CancellationToken ct = default)
        {
            lock(_lock) { return Task.FromResult(_processValue); }
        }
    }
}