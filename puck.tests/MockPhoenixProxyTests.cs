using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using puck.Services.IoBus;
using Xunit;

namespace Puck.Tests
{
    public class MockPhoenixProxyTests
    {
        [Fact]
        public void InitializesWithDefaultRanges()
        {
            var mock = new MockPhoenixProxy();
            Assert.Equal(8, mock.DigitalInputState.Count);
            Assert.Equal(4, mock.DigitalOutputState.Count);
            Assert.Equal(4, mock.AnalogInputState.Count);
            Assert.Equal(2, mock.AnalogOutputState.Count);
        }

        [Fact]
        public void InitializesWithCustomRanges()
        {
            var mock = new MockPhoenixProxy(
                digitalInputs: new ushort[] { 10, 20 },
                digitalOutputs: new ushort[] { 30 },
                analogInputs: new ushort[] { 40, 41, 42 },
                analogOutputs: new ushort[] { 50 });
            Assert.Equal(new ushort[] { 10, 20 }, mock.DigitalInputState.Keys.OrderBy(x => x));
            Assert.Equal(new ushort[] { 30 }, mock.DigitalOutputState.Keys);
            Assert.Equal(new ushort[] { 40, 41, 42 }, mock.AnalogInputState.Keys.OrderBy(x => x));
            Assert.Equal(new ushort[] { 50 }, mock.AnalogOutputState.Keys);
        }

        [Fact]
        public async Task SetDigitalOutputStateAsync_UpdatesState()
        {
            var mock = new MockPhoenixProxy();
            await mock.SetDigitalOutputStateAsync(1, true, CancellationToken.None);
            Assert.True(mock.DigitalOutputState[1]?.State);
            await mock.SetDigitalOutputStateAsync(1, false, CancellationToken.None);
            Assert.False(mock.DigitalOutputState[1]?.State);
        }

        [Fact]
        public async Task SetAnalogOutputStateAsync_UpdatesState()
        {
            var mock = new MockPhoenixProxy();
            await mock.SetAnalogOutputStateAsync(1, 123.45, CancellationToken.None);
            Assert.Equal(123.45, mock.AnalogOutputState[1]?.State);
        }

        [Fact]
        public void SetDigitalInput_UpdatesState()
        {
            var mock = new MockPhoenixProxy();
            mock.SetDigitalInput(1, true);
            Assert.True(mock.DigitalInputState[1]?.State);
            mock.SetDigitalInput(1, false);
            Assert.False(mock.DigitalInputState[1]?.State);
        }

        [Fact]
        public void SetAnalogInput_UpdatesState()
        {
            var mock = new MockPhoenixProxy();
            mock.SetAnalogInput(1, 99.9);
            Assert.Equal(99.9, mock.AnalogInputState[1]?.State);
        }

        [Fact]
        public async Task SetDigitalOutputStateAsync_InvalidIndex_Throws()
        {
            var mock = new MockPhoenixProxy();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mock.SetDigitalOutputStateAsync(99, true, CancellationToken.None));
        }

        [Fact]
        public async Task SetAnalogOutputStateAsync_InvalidIndex_Throws()
        {
            var mock = new MockPhoenixProxy();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mock.SetAnalogOutputStateAsync(99, 1.0, CancellationToken.None));
        }

        [Fact]
        public void SetDigitalInput_InvalidIndex_Throws()
        {
            var mock = new MockPhoenixProxy();
            Assert.Throws<ArgumentOutOfRangeException>(() => mock.SetDigitalInput(99, true));
        }

        [Fact]
        public void SetAnalogInput_InvalidIndex_Throws()
        {
            var mock = new MockPhoenixProxy();
            Assert.Throws<ArgumentOutOfRangeException>(() => mock.SetAnalogInput(99, 1.0));
        }

        [Fact]
        public async Task SetDigitalOutputStateAsync_Cancellation_Throws()
        {
            var mock = new MockPhoenixProxy();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => mock.SetDigitalOutputStateAsync(1, true, cts.Token));
        }

        [Fact]
        public async Task SetAnalogOutputStateAsync_Cancellation_Throws()
        {
            var mock = new MockPhoenixProxy();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => mock.SetAnalogOutputStateAsync(1, 1.0, cts.Token));
        }

        [Fact]
        public void Disposed_ThrowsOnAnyOperation()
        {
            var mock = new MockPhoenixProxy();
            mock.Dispose();
            Assert.Throws<ObjectDisposedException>(() => mock.SetDigitalInput(1, true));
            Assert.Throws<ObjectDisposedException>(() => mock.SetAnalogInput(1, 1.0));
            // Accessing property is fine, so do not assert exception here
        }

        [Fact]
        public async Task Disposed_ThrowsOnAsyncMethods()
        {
            var mock = new MockPhoenixProxy();
            mock.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => mock.SetDigitalOutputStateAsync(1, true, CancellationToken.None));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => mock.SetAnalogOutputStateAsync(1, 1.0, CancellationToken.None));
        }
    }
} 