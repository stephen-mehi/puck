using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace puck.Services.IoBus
{
    public class MockPhoenixProxy : IPhoenixProxy
    {
        private readonly Dictionary<ushort, DigitalIoState?> _digitalInputState = new();
        private readonly Dictionary<ushort, DigitalIoState?> _digitalOutputState = new();
        private readonly Dictionary<ushort, AnalogIoState?> _analogInputState = new();
        private readonly Dictionary<ushort, AnalogIoState?> _analogOutputState = new();
        private bool _disposed;

        public MockPhoenixProxy(
            IEnumerable<ushort>? digitalInputs = null,
            IEnumerable<ushort>? digitalOutputs = null,
            IEnumerable<ushort>? analogInputs = null,
            IEnumerable<ushort>? analogOutputs = null)
        {
            foreach (var i in digitalInputs ?? Enumerable.Range(1, 8).Select(x => (ushort)x))
                _digitalInputState[i] = new DigitalIoState(false, DateTime.UtcNow);
            foreach (var i in digitalOutputs ?? Enumerable.Range(1, 4).Select(x => (ushort)x))
                _digitalOutputState[i] = new DigitalIoState(false, DateTime.UtcNow);
            foreach (var i in analogInputs ?? Enumerable.Range(1, 4).Select(x => (ushort)x))
                _analogInputState[i] = new AnalogIoState(0.0, DateTime.UtcNow);
            foreach (var i in analogOutputs ?? Enumerable.Range(1, 2).Select(x => (ushort)x))
                _analogOutputState[i] = new AnalogIoState(0.0, DateTime.UtcNow);
        }

        public IReadOnlyDictionary<ushort, DigitalIoState?> DigitalInputState => _digitalInputState;
        public IReadOnlyDictionary<ushort, DigitalIoState?> DigitalOutputState => _digitalOutputState;
        public IReadOnlyDictionary<ushort, AnalogIoState?> AnalogInputState => _analogInputState;
        public IReadOnlyDictionary<ushort, AnalogIoState?> AnalogOutputState => _analogOutputState;

        public Task SetDigitalOutputStateAsync(ushort index, bool state, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            if (!_digitalOutputState.ContainsKey(index))
                throw new ArgumentOutOfRangeException(nameof(index), $"Digital output index {index} not found.");

            _digitalOutputState[index] = new DigitalIoState(state, DateTime.UtcNow);
            return Task.CompletedTask;
        }

        public Task SetAnalogOutputStateAsync(ushort index, double state, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            if (!_analogOutputState.ContainsKey(index))
                throw new ArgumentOutOfRangeException(nameof(index), $"Analog output index {index} not found.");
            _analogOutputState[index] = new AnalogIoState(state, DateTime.UtcNow);
            return Task.CompletedTask;
        }

        // Optionally, allow test code to set input states
        public void SetDigitalInput(ushort index, bool state)
        {
            ThrowIfDisposed();
            if (!_digitalInputState.ContainsKey(index))
                throw new ArgumentOutOfRangeException(nameof(index), $"Digital input index {index} not found.");
            _digitalInputState[index] = new DigitalIoState(state, DateTime.UtcNow);
        }

        public void SetAnalogInput(ushort index, double state)
        {
            ThrowIfDisposed();
            if (!_analogInputState.ContainsKey(index))
                throw new ArgumentOutOfRangeException(nameof(index), $"Analog input index {index} not found.");
            _analogInputState[index] = new AnalogIoState(state, DateTime.UtcNow);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MockPhoenixProxy));
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
} 