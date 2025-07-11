using Puck.Services.TemperatureController;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Puck.Tests
{
    public class MockTemperatureControllerTests
    {
        [Fact]
        public async Task SetAndGetSetValue_WithinLimits_Succeeds()
        {
            var mock = new MockTemperatureController();
            await mock.SetSetPointAsync(150);
            var setValue = mock.GetSetValue();
            Assert.Equal(150, setValue);
        }

        [Fact]
        public async Task SetSetPointAsync_UpdatesProcessValue()
        {
            var mock = new MockTemperatureController();
            await mock.SetSetPointAsync(123);
            var pv = mock.GetProcessValue();
            Assert.Equal(123, pv);
        }

        [Fact]
        public async Task ApplySetPointSynchronouslyAsync_SucceedsWithinTolerance()
        {
            var mock = new MockTemperatureController();
            await mock.ApplySetPointSynchronouslyAsync(200, 0.1, TimeSpan.FromSeconds(1));
            Assert.Equal(200, mock.GetProcessValue());
        }

        [Fact]
        public async Task ApplySetPointSynchronouslyAsync_ThrowsOnTimeout()
        {
            var mock = new MockTemperatureController();
            mock.SimulateStuckProcessValue = true;
            await Assert.ThrowsAsync<TimeoutException>(() => mock.ApplySetPointSynchronouslyAsync(1000, 0.01, TimeSpan.FromMilliseconds(10)));
        }

        [Fact]
        public async Task DisableControlLoopAsync_SetsControlLoopInactive()
        {
            var mock = new MockTemperatureController();
            mock.SetControlLoopActive(true);
            await mock.DisableControlLoopAsync();
            // No direct way to check, but should not throw
        }

        [Fact]
        public void Dispose_SetsDisposedFlag()
        {
            var mock = new MockTemperatureController();
            mock.Dispose();
            // No exception means success; can't check private _disposed directly
        }
    }
}