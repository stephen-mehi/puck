using System;
using System.Threading;
using System.Threading.Tasks;

namespace Puck.Services.TemperatureController
{
    public interface ITemperatureController : IDisposable
    {
        Task SetSetPointAsync(int setpoint, CancellationToken ct = default);
        Task ApplySetPointSynchronouslyAsync(int tempSetPoint, double tolerance, TimeSpan timeout, CancellationToken ct = default);
        Task<double> GetSetValueAsync(CancellationToken ct = default);
        Task<double> GetProcessValueAsync(CancellationToken ct = default);
        Task DisableControlLoopAsync(CancellationToken ct = default);
        Task EnableControlLoopAsync(CancellationToken ct = default);
    }
}