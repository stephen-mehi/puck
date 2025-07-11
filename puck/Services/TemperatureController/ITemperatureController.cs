using System;
using System.Threading;
using System.Threading.Tasks;

namespace Puck.Services.TemperatureController
{
    public interface ITemperatureController : IDisposable
    {
        Task SetSetPointAsync(int setpoint, CancellationToken ct = default);
        Task ApplySetPointSynchronouslyAsync(int tempSetPoint, double tolerance, TimeSpan timeout, CancellationToken ct = default);
        double? GetSetValue();
        double? GetProcessValue();
        Task DisableControlLoopAsync(CancellationToken ct = default);
    }
}