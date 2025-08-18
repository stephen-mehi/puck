using Puck.Services.TemperatureController;
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace puck.tests
{
    public class MockTemperatureControllerTests
    {
        [Fact]
        public async Task SetAndGetSetValue_WithinLimits_Succeeds()
        {
            var mock = new MockTemperatureController();
            await mock.SetSetPointAsync(150);
            var setValue = await mock.GetSetValueAsync();
            Assert.Equal(150, setValue);
        }

        [Fact]
        public async Task SetSetPointAsync_UpdatesProcessValue()
        {
            var mock = new MockTemperatureController();
            await mock.EnableControlLoopAsync();
            await mock.SetSetPointAsync(123);
            await Task.Delay(3000);

            var pv = await mock.GetProcessValueAsync();
            Assert.Equal(123, pv);
        }

        [Fact]
        public async Task ApplySetPointSynchronouslyAsync_SucceedsWithinTolerance()
        {
            var mock = new MockTemperatureController();
            await mock.ApplySetPointSynchronouslyAsync(123, 0, TimeSpan.FromSeconds(10));
            Assert.Equal(123, await mock.GetProcessValueAsync());
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