using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NModbus;

namespace puck.Services.IoBus
{
    /// <summary>
    /// High-fidelity, thread-safe Modbus device simulator implementing IModbus.
    /// - Persists state across operations per slave address
    /// - Supports coils, discrete inputs, holding registers, input registers, and file records
    /// - Enforces basic Modbus contiguous range semantics
    /// - Provides async variants with optional simulated latency
    /// </summary>
    public sealed class SimulatedModbus : IModbus
    {
        private sealed class SlaveMemory
        {
            public readonly Dictionary<ushort, bool> Coils = new Dictionary<ushort, bool>();
            public readonly Dictionary<ushort, bool> Inputs = new Dictionary<ushort, bool>();
            public readonly Dictionary<ushort, ushort> HoldingRegisters = new Dictionary<ushort, ushort>();
            public readonly Dictionary<ushort, ushort> InputRegisters = new Dictionary<ushort, ushort>();
            // FileNumber -> (StartingAddress -> data bytes)
            public readonly Dictionary<ushort, Dictionary<ushort, byte[]>> FileRecords = new Dictionary<ushort, Dictionary<ushort, byte[]>>();
        }

        private readonly object _sync = new object();
        private readonly Dictionary<byte, SlaveMemory> _slaves = new Dictionary<byte, SlaveMemory>();
        private readonly int _simulatedLatencyMs;
        private bool _isDisposed;

        /// <summary>
        /// Create a simulator.
        /// </summary>
        /// <param name="simulatedLatencyMs">Optional artificial latency for async methods (ms).</param>
        public SimulatedModbus(int simulatedLatencyMs = 0)
        {
            _simulatedLatencyMs = Math.Max(0, simulatedLatencyMs);
        }

        private SlaveMemory GetOrCreateSlave(byte slaveAddress)
        {
            lock (_sync)
            {
                if (!_slaves.TryGetValue(slaveAddress, out var mem))
                {
                    mem = new SlaveMemory();
                    _slaves[slaveAddress] = mem;
                }
                return mem;
            }
        }

        // ---- Test helpers to seed device state (input registers / discrete inputs) ----
        public void SeedInputRegister(byte slaveAddress, ushort address, ushort value)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                mem.InputRegisters[address] = value;
            }
        }

        public void SeedInputRegisters(byte slaveAddress, ushort startAddress, ushort[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            ThrowIfDisposed();
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                for (int i = 0; i < values.Length; i++)
                    mem.InputRegisters[(ushort)(startAddress + i)] = values[i];
            }
        }

        private static void ValidateRange(ushort startAddress, ushort numberOfPoints)
        {
            if (numberOfPoints == 0)
                throw new ArgumentOutOfRangeException(nameof(numberOfPoints), "numberOfPoints must be >= 1");
            // Prevent overflow in address + count - 1
            if ((uint)startAddress + (uint)numberOfPoints - 1u > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(numberOfPoints), "Requested range exceeds address space");
        }

        private static bool[] SnapshotBits(Dictionary<ushort, bool> map, ushort start, ushort count)
        {
            var result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                var addr = (ushort)(start + i);
                result[i] = map.TryGetValue(addr, out var v) ? v : false;
            }
            return result;
        }

        private static ushort[] SnapshotRegisters(Dictionary<ushort, ushort> map, ushort start, ushort count)
        {
            var result = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                var addr = (ushort)(start + i);
                result[i] = map.TryGetValue(addr, out var v) ? v : (ushort)0;
            }
            return result;
        }

        private static void WriteBits(Dictionary<ushort, bool> map, ushort start, bool[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                map[(ushort)(start + i)] = data[i];
            }
        }

        private static void WriteRegisters(Dictionary<ushort, ushort> map, ushort start, ushort[] data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                map[(ushort)(start + i)] = data[i];
            }
        }

        public bool[] ReadCoils(byte slaveAddress, ushort startAddress, ushort numberOfPoints)
        {
            ThrowIfDisposed();
            ValidateRange(startAddress, numberOfPoints);
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                return SnapshotBits(mem.Coils, startAddress, numberOfPoints);
            }
        }

        public Task<bool[]> ReadCoilsAsync(byte slaveAddress, ushort startAddress, ushort numberOfPoints)
        {
            var res = ReadCoils(slaveAddress, startAddress, numberOfPoints);
            return CompleteWithLatency(res);
        }

        public bool[] ReadInputs(byte slaveAddress, ushort startAddress, ushort numberOfPoints)
        {
            ThrowIfDisposed();
            ValidateRange(startAddress, numberOfPoints);
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                return SnapshotBits(mem.Inputs, startAddress, numberOfPoints);
            }
        }

        public Task<bool[]> ReadInputsAsync(byte slaveAddress, ushort startAddress, ushort numberOfPoints)
        {
            var res = ReadInputs(slaveAddress, startAddress, numberOfPoints);
            return CompleteWithLatency(res);
        }

        public ushort[] ReadHoldingRegisters(byte slaveAddress, ushort startAddress, ushort numberOfPoints)
        {
            ThrowIfDisposed();
            ValidateRange(startAddress, numberOfPoints);
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                return SnapshotRegisters(mem.HoldingRegisters, startAddress, numberOfPoints);
            }
        }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte slaveAddress, ushort startAddress, ushort numberOfPoints)
        {
            var res = ReadHoldingRegisters(slaveAddress, startAddress, numberOfPoints);
            return CompleteWithLatency(res);
        }

        public ushort[] ReadInputRegisters(byte slaveAddress, ushort startAddress, ushort numberOfPoints)
        {
            ThrowIfDisposed();
            ValidateRange(startAddress, numberOfPoints);
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                return SnapshotRegisters(mem.InputRegisters, startAddress, numberOfPoints);
            }
        }

        public Task<ushort[]> ReadInputRegistersAsync(byte slaveAddress, ushort startAddress, ushort numberOfPoints)
        {
            var res = ReadInputRegisters(slaveAddress, startAddress, numberOfPoints);
            return CompleteWithLatency(res);
        }

        public void WriteSingleCoil(byte slaveAddress, ushort coilAddress, bool value)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                mem.Coils[coilAddress] = value;
            }
        }

        public Task WriteSingleCoilAsync(byte slaveAddress, ushort coilAddress, bool value)
        {
            WriteSingleCoil(slaveAddress, coilAddress, value);
            return CompleteWithLatency();
        }

        public void WriteSingleRegister(byte slaveAddress, ushort registerAddress, ushort value)
        {
            ThrowIfDisposed();
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                mem.HoldingRegisters[registerAddress] = value;

                // Basic echo/mirroring for known Inline process data:
                // DO OUT word at 8009 is readable at same address via IN map
                // AO config OUT 8014/8015 echoed at IN 8005/8006
                // AO data OUT 8016/8017 echoed at IN 8007/8008
                ushort inAddr = registerAddress switch
                {
                    8009 => (ushort)8009,
                    8014 => (ushort)8005,
                    8015 => (ushort)8006,
                    8016 => (ushort)8007,
                    8017 => (ushort)8008,
                    _ => (ushort)0
                };
                if (inAddr != 0)
                    mem.InputRegisters[inAddr] = value;
            }
        }

        public Task WriteSingleRegisterAsync(byte slaveAddress, ushort registerAddress, ushort value)
        {
            WriteSingleRegister(slaveAddress, registerAddress, value);
            return CompleteWithLatency();
        }

        public void WriteMultipleRegisters(byte slaveAddress, ushort startAddress, ushort[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0) return;
            ThrowIfDisposed();
            ValidateRange(startAddress, (ushort)data.Length);
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                WriteRegisters(mem.HoldingRegisters, startAddress, data);
            }
        }

        public Task WriteMultipleRegistersAsync(byte slaveAddress, ushort startAddress, ushort[] data)
        {
            WriteMultipleRegisters(slaveAddress, startAddress, data);
            return CompleteWithLatency();
        }

        public void WriteMultipleCoils(byte slaveAddress, ushort startAddress, bool[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length == 0) return;
            ThrowIfDisposed();
            ValidateRange(startAddress, (ushort)data.Length);
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                WriteBits(mem.Coils, startAddress, data);
            }
        }

        public Task WriteMultipleCoilsAsync(byte slaveAddress, ushort startAddress, bool[] data)
        {
            WriteMultipleCoils(slaveAddress, startAddress, data);
            return CompleteWithLatency();
        }

        public ushort[] ReadWriteMultipleRegisters(byte slaveAddress, ushort startReadAddress, ushort numberOfPointsToRead, ushort startWriteAddress, ushort[] writeData)
        {
            if (writeData == null) throw new ArgumentNullException(nameof(writeData));
            ThrowIfDisposed();
            ValidateRange(startReadAddress, numberOfPointsToRead);
            ValidateRange(startWriteAddress, (ushort)writeData.Length);
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAddress);
                // Perform write first
                WriteRegisters(mem.HoldingRegisters, startWriteAddress, writeData);
                // Then read
                return SnapshotRegisters(mem.HoldingRegisters, startReadAddress, numberOfPointsToRead);
            }
        }

        public Task<ushort[]> ReadWriteMultipleRegistersAsync(byte slaveAddress, ushort startReadAddress, ushort numberOfPointsToRead, ushort startWriteAddress, ushort[] writeData)
        {
            var res = ReadWriteMultipleRegisters(slaveAddress, startReadAddress, numberOfPointsToRead, startWriteAddress, writeData);
            return CompleteWithLatency(res);
        }

        public void WriteFileRecord(byte slaveAdress, ushort fileNumber, ushort startingAddress, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            ThrowIfDisposed();
            lock (_sync)
            {
                var mem = GetOrCreateSlave(slaveAdress);
                if (!mem.FileRecords.TryGetValue(fileNumber, out var file))
                {
                    file = new Dictionary<ushort, byte[]>();
                    mem.FileRecords[fileNumber] = file;
                }
                // Store a copy to maintain immutability to callers
                var copy = new byte[data.Length];
                Array.Copy(data, copy, data.Length);
                file[startingAddress] = copy;
            }
        }

        public TResponse ExecuteCustomMessage<TResponse>(IModbusMessage request) where TResponse : IModbusMessage, new()
        {
            ThrowIfDisposed();
            // Basic stub: return a new response instance. Consumers relying on vendor-specific
            // custom messages should extend this simulator to interpret and respond accordingly.
            return new TResponse();
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(SimulatedModbus));
        }

        private async Task CompleteWithLatency()
        {
            if (_simulatedLatencyMs > 0)
                await Task.Delay(_simulatedLatencyMs).ConfigureAwait(false);
        }

        private async Task<T> CompleteWithLatency<T>(T value)
        {
            if (_simulatedLatencyMs > 0)
                await Task.Delay(_simulatedLatencyMs).ConfigureAwait(false);
            return value;
        }

        public void Dispose()
        {
            _isDisposed = true;
            lock (_sync)
            {
                _slaves.Clear();
            }
        }
    }
}


