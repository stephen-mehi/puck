using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NModbus;
using puck.Services.IoBus;
using Xunit;

namespace puck.tests
{
    public class SimulatedModbusTests
    {
        private sealed class DummyRequest : IModbusMessage
        {
            public byte FunctionCode { get; set; } = 0x2B;
            public byte[] MessageFrame { get; set; } = Array.Empty<byte>();
            public byte[] ProtocolDataUnit { get; set; } = Array.Empty<byte>();
            public byte SlaveAddress { get; set; } = 1;
            public ushort TransactionId { get; set; } = 0;
            public void Initialize(byte[] frame) { MessageFrame = frame; }
            public void ValidateResponse(IModbusMessage response) { }
        }

        private sealed class DummyResponse : IModbusMessage
        {
            public byte FunctionCode { get; set; } = 0x2B;
            public byte[] MessageFrame { get; set; } = Array.Empty<byte>();
            public byte[] ProtocolDataUnit { get; set; } = Array.Empty<byte>();
            public byte SlaveAddress { get; set; } = 1;
            public ushort TransactionId { get; set; } = 0;
            public void Initialize(byte[] frame) { MessageFrame = frame; }
            public void ValidateResponse(IModbusMessage response) { }
        }

        [Fact]
        public void ReadDefaults_ReturnsZeroOrFalse()
        {
            using var sim = new SimulatedModbus();
            var coils = sim.ReadCoils(1, 0, 10);
            var inputs = sim.ReadInputs(1, 0, 10);
            var hr = sim.ReadHoldingRegisters(1, 100, 5);
            var ir = sim.ReadInputRegisters(1, 100, 5);
            Assert.All(coils, v => Assert.False(v));
            Assert.All(inputs, v => Assert.False(v));
            Assert.All(hr, v => Assert.Equal((ushort)0, v));
            Assert.All(ir, v => Assert.Equal((ushort)0, v));
        }

        [Fact]
        public void WriteSingleCoil_ThenReadReflects()
        {
            using var sim = new SimulatedModbus();
            sim.WriteSingleCoil(1, 3, true);
            var coils = sim.ReadCoils(1, 0, 5);
            Assert.False(coils[0]);
            Assert.True(coils[3]);
        }

        [Fact]
        public void WriteMultipleCoils_ThenReadReflects()
        {
            using var sim = new SimulatedModbus();
            sim.WriteMultipleCoils(1, 10, new[] { true, false, true, true });
            var r = sim.ReadCoils(1, 8, 6);
            Assert.False(r[0]); // 8
            Assert.False(r[1]); // 9
            Assert.True(r[2]);  // 10
            Assert.False(r[3]); // 11
            Assert.True(r[4]);  // 12
            Assert.True(r[5]);  // 13
        }

        [Fact]
        public void WriteSingleRegister_ThenReadReflects()
        {
            using var sim = new SimulatedModbus();
            sim.WriteSingleRegister(1, 500, 0xBEEF);
            var hr = sim.ReadHoldingRegisters(1, 498, 5);
            Assert.Equal(0, hr[0]);
            Assert.Equal(0, hr[1]);
            Assert.Equal(0xBEEF, hr[2]);
            Assert.Equal(0, hr[3]);
            Assert.Equal(0, hr[4]);
        }

        [Fact]
        public void WriteMultipleRegisters_ThenReadReflects()
        {
            using var sim = new SimulatedModbus();
            sim.WriteMultipleRegisters(1, 1000, new ushort[] { 1, 2, 3, 4, 5 });
            var hr = sim.ReadHoldingRegisters(1, 998, 8);
            Assert.Equal(new ushort[] { 0, 0, 1, 2, 3, 4, 5, 0 }, hr);
        }

        [Fact]
        public void ReadWriteMultipleRegisters_WritesThenReadsSnapshot()
        {
            using var sim = new SimulatedModbus();
            sim.WriteMultipleRegisters(1, 10, new ushort[] { 10, 11, 12 });
            var read = sim.ReadWriteMultipleRegisters(1, 8, 6, 11, new ushort[] { 111, 112 });
            // After write, holding[11]=111, holding[12]=112
            Assert.Equal(new ushort[] { 0, 0, 10, 111, 112, 0 }, read);
        }

        [Fact]
        public void RangeValidation_ThrowsOnZeroCountAndOverflow()
        {
            using var sim = new SimulatedModbus();
            Assert.Throws<ArgumentOutOfRangeException>(() => sim.ReadCoils(1, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => sim.ReadHoldingRegisters(1, 65535, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => sim.WriteMultipleRegisters(1, 65535, new ushort[] { 1, 2 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => sim.WriteMultipleCoils(1, 65535, new bool[] { true, false }));
        }

        [Fact]
        public void MultipleSlaves_IsolatedState()
        {
            using var sim = new SimulatedModbus();
            sim.WriteSingleCoil(1, 1, true);
            sim.WriteSingleRegister(2, 100, 1234);
            Assert.True(sim.ReadCoils(1, 1, 1)[0]);
            Assert.False(sim.ReadCoils(2, 1, 1)[0]);
            Assert.Equal(0, sim.ReadHoldingRegisters(1, 100, 1)[0]);
            Assert.Equal(1234, sim.ReadHoldingRegisters(2, 100, 1)[0]);
        }

        [Fact]
        public async Task AsyncOperations_RespectLatency_AndPersist()
        {
            using var sim = new SimulatedModbus(simulatedLatencyMs: 2);
            var sw = Stopwatch.StartNew();
            await sim.WriteSingleCoilAsync(1, 7, true);
            var elapsed1 = sw.ElapsedMilliseconds;
            Assert.True(elapsed1 >= 2);
            sw.Restart();
            var coils = await sim.ReadCoilsAsync(1, 0, 8);
            var elapsed2 = sw.ElapsedMilliseconds;
            Assert.True(elapsed2 >= 2);
            Assert.True(coils[7]);
        }

        [Fact]
        public void Dispose_ThrowsOnUse()
        {
            var sim = new SimulatedModbus();
            sim.Dispose();
            Assert.Throws<ObjectDisposedException>(() => sim.ReadCoils(1, 0, 1));
            Assert.Throws<ObjectDisposedException>(() => sim.WriteSingleRegister(1, 0, 0));
        }

        [Fact]
        public void ExecuteCustomMessage_ReturnsNewResponse()
        {
            using var sim = new SimulatedModbus();
            var resp = sim.ExecuteCustomMessage<DummyResponse>(new DummyRequest());
            Assert.NotNull(resp);
            Assert.IsType<DummyResponse>(resp);
        }

        [Fact]
        public void WriteFileRecord_AcceptsData()
        {
            using var sim = new SimulatedModbus();
            sim.WriteFileRecord(1, fileNumber: 10, startingAddress: 0, data: new byte[] { 1, 2, 3 });
            sim.WriteFileRecord(1, fileNumber: 10, startingAddress: 100, data: new byte[] { 9, 9 });
            // No direct read API; confirm regular operations still fine
            sim.WriteSingleCoil(1, 0, true);
            Assert.True(sim.ReadCoils(1, 0, 1)[0]);
        }

        [Fact]
        public async Task Concurrency_WritesAndReadsRemainConsistent()
        {
            using var sim = new SimulatedModbus();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int tasks = 8;
            int len = 200;
            var writers = Enumerable.Range(0, tasks).Select(async t =>
            {
                var rand = new Random(1234 + t);
                while (!cts.IsCancellationRequested)
                {
                    int baseAddr = rand.Next(0, 1000);
                    var data = Enumerable.Range(0, len).Select(i => (ushort)(baseAddr + i)).ToArray();
                    sim.WriteMultipleRegisters(1, (ushort)(baseAddr % 5000), data);
                    await Task.Yield();
                }
            }).ToArray();

            var reader = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var hr = sim.ReadHoldingRegisters(1, 0, 10);
                    // Only assertion is that call does not throw and returns expected length
                    Assert.Equal(10, hr.Length);
                    await Task.Yield();
                }
            });

            await Task.WhenAll(Task.WhenAll(writers), reader);
        }
    }
}


