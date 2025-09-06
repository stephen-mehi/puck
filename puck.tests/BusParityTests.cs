using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NModbus;
using PhoenixInline.Simple;
using puck.Services.IoBus;
using Xunit;

namespace puck.tests
{
    public class BusParityTests
    {
        private const ushort REG_MODULE_COUNT = 1400;
        private const ushort REG_MODULE_INFO = 1401;
        private const ushort PD_IN_START = 8000;

        // Layout: DI(1 word IN), DO(1 word OUT), AI(4 IN), AO(4 IN echo + 4 OUT cfg/data)
        private static void SeedStationLayout(SimulatedModbus sim)
        {
            // Module count = 4
            sim.SeedInputRegister(0, REG_MODULE_COUNT, 4);
            // Info words: hi=length code bits (words), lo=id code
            // DI id=190, DO id=189, AI id=127, AO id=91 (per code)
            sim.SeedInputRegister(0, (ushort)(REG_MODULE_INFO + 0), (ushort)((0x01 << 8) | 190));
            sim.SeedInputRegister(0, (ushort)(REG_MODULE_INFO + 1), (ushort)((0x01 << 8) | 189));
            sim.SeedInputRegister(0, (ushort)(REG_MODULE_INFO + 2), (ushort)((0x04 << 8) | 127));
            sim.SeedInputRegister(0, (ushort)(REG_MODULE_INFO + 3), (ushort)((0x04 << 8) | 91));
        }

        private static (IModbusMaster master, SimulatedModbus sim) CreateMasterBackedBySim()
        {
            var sim = new SimulatedModbus();
            SeedStationLayout(sim);

            var mock = new Mock<IModbusMaster>(MockBehavior.Strict);
            mock.Setup(m => m.ReadInputRegistersAsync(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<ushort>()))
                .Returns<byte, ushort, ushort>((slave, start, count) => sim.ReadInputRegistersAsync(slave, start, count));
            mock.Setup(m => m.WriteSingleRegisterAsync(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<ushort>()))
                .Returns<byte, ushort, ushort>((slave, addr, value) => sim.WriteSingleRegisterAsync(slave, addr, value));
            mock.Setup(m => m.Dispose());
            return (mock.Object, sim);
        }

        private static LinearInlineIoBus CreateLinearBus(IModbusMaster master)
        {
            Func<CancellationToken, Task<IModbusMaster>> factory = _ => Task.FromResult(master);
            return new LinearInlineIoBus(factory);
        }

        private static IOBus CreateIOBusUsingSimActions(IModbusMaster master, SimulatedModbus sim)
        {
            // Use the same master backed by SimulatedModbus so IsConnectedAsync also probes the sim

            // DIGITAL INPUTS: single module (moduleId=1), word at 8000
            var di_idxToModId = Enumerable.Range(1, 16).ToDictionary(i => (ushort)i, _ => (ushort)1);
            var di_busToLocal = Enumerable.Range(1, 16).ToDictionary(i => (ushort)i, i => (ushort)i);
            var di_read = new Dictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, bool>>>>
            {
                [1] = async (locals, ct) =>
                {
                    var word = (await sim.ReadInputRegistersAsync(0, PD_IN_START + 0, 1))[0];
                    var map = new Dictionary<ushort, bool>();
                    foreach (var idx in locals) map[(ushort)idx] = ((word >> (idx - 1)) & 1) == 1;
                    return map;
                }
            };
            var di_infoMap = Enumerable.Range(1, 16).ToDictionary(i => new ModuleIdIOIndexPair(1, (ushort)i), i => (ushort)i);
            var diModel = new DigitalInputModulesActionsModel(di_idxToModId, di_busToLocal, di_read, di_infoMap);

            // DIGITAL OUTPUTS: single module (moduleId=2), word at 8009
            var do_idxToModId = Enumerable.Range(1, 16).ToDictionary(i => (ushort)i, _ => (ushort)2);
            var do_busToLocal = Enumerable.Range(1, 16).ToDictionary(i => (ushort)i, i => (ushort)i);
            var do_write = new Dictionary<ushort, Func<IReadOnlyDictionary<ushort, bool>, CancellationToken, Task>>
            {
                [2] = async (points, ct) =>
                {
                    var word = (await sim.ReadInputRegistersAsync(0, PD_IN_START + 9, 1))[0];
                    foreach (var p in points)
                    {
                        if (p.Value) word = (ushort)(word | (1 << (p.Key - 1)));
                        else word = (ushort)(word & ~(1 << (p.Key - 1)));
                    }
                    await sim.WriteSingleRegisterAsync(0, (ushort)(PD_IN_START + 9), word);
                }
            };
            var do_read = new Dictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, bool>>>>
            {
                [2] = async (locals, ct) =>
                {
                    var word = (await sim.ReadInputRegistersAsync(0, PD_IN_START + 9, 1))[0];
                    var map = new Dictionary<ushort, bool>();
                    foreach (var idx in locals) map[(ushort)idx] = ((word >> (idx - 1)) & 1) == 1;
                    return map;
                }
            };
            var do_infoMap = Enumerable.Range(1, 16).ToDictionary(i => new ModuleIdIOIndexPair(2, (ushort)i), i => (ushort)i);
            var doModel = new DigitalOutputModulesActionsModel(do_idxToModId, do_busToLocal, do_write, do_read, do_infoMap);

            // ANALOG INPUTS: single module (moduleId=3), words at 8001..8004; return doubles using same mapping as LinearInlineIoBus
            var ai_idxToModId = Enumerable.Range(1, 4).ToDictionary(i => (ushort)i, _ => (ushort)3);
            var ai_busToLocal = Enumerable.Range(1, 4).ToDictionary(i => (ushort)i, i => (ushort)i);
            var ai_read = new Dictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, double>>>>
            {
                [3] = async (locals, ct) =>
                {
                    var map = new Dictionary<ushort, double>();
                    foreach (var idx in locals)
                    {
                        var raw = (await sim.ReadInputRegistersAsync(0, (ushort)(PD_IN_START + idx), 1))[0];
                        raw = (ushort)(raw & 0xFFF8);
                        double val = Map(raw, 0, 32512, 4.0, 21.339); // 4-20 mA mapping (same as LinearInline)
                        map[(ushort)idx] = val;
                    }
                    return map;
                }
            };
            var ai_infoMap = Enumerable.Range(1, 4).ToDictionary(i => new ModuleIdIOIndexPair(3, (ushort)i), i => (ushort)i);
            var aiModel = new AnalogInputModulesActionsModel(ai_busToLocal, ai_idxToModId, ai_read, ai_infoMap);

            // ANALOG OUTPUTS: single module (moduleId=4)
            // AO IN echo base = 8005 (+0, +1) for cfg echo; measurement at +2,+3; OUT base = 8014, write data at +2,+3
            var ao_idxToModId = Enumerable.Range(1, 2).ToDictionary(i => (ushort)i, _ => (ushort)4);
            var ao_busToLocal = Enumerable.Range(1, 2).ToDictionary(i => (ushort)i, i => (ushort)i);
            var ao_write = new Dictionary<ushort, Func<IReadOnlyDictionary<ushort, double>, CancellationToken, Task>>
            {
                [4] = async (vals, ct) =>
                {
                    foreach (var kv in vals)
                    {
                        ushort ch = kv.Key; double value = kv.Value;
                        // Convert double to raw like LinearInline
                        ushort raw = ToRawFromEngineering(value, ch);
                        ushort addr = (ushort)(8014 + 2 + (ch - 1));
                        await sim.WriteSingleRegisterAsync(0, addr, raw);
                    }
                }
            };
            var ao_read = new Dictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, double>>>>
            {
                [4] = async (locals, ct) =>
                {
                    var map = new Dictionary<ushort, double>();
                    foreach (var idx in locals)
                    {
                        ushort addr = (ushort)(8005 + 2 + (idx - 1));
                        var raw = (await sim.ReadInputRegistersAsync(0, addr, 1))[0];
                        raw = (ushort)(raw & 0xFFF8);
                        double val = Map(raw, 0, 32512, 0.0, 10.837); // assume V 0-10 by default
                        map[(ushort)idx] = val;
                    }
                    return map;
                }
            };
            var ao_infoMap = Enumerable.Range(1, 2).ToDictionary(i => new ModuleIdIOIndexPair(4, (ushort)i), i => (ushort)i);
            var aoModel = new AnalogOutputModulesActionsModel(ao_busToLocal, ao_idxToModId, ao_write, ao_read, ao_infoMap);

            return new IOBus(master, diModel, doModel, aiModel, aoModel);
        }

        private static ushort ToRawFromEngineering(double value, int channel)
        {
            // Assume AO default range V_0_10. LinearInline maps using Map(raw, 0, 32512, 0..10.837)
            double clamped = Math.Max(0, Math.Min(10.837, value));
            double raw = (clamped / 10.837) * 32512.0;
            return (ushort)((int)Math.Round(raw) & 0xFFF8);
        }

        private static double Map(double x, double inMin, double inMax, double outMin, double outMax)
            => (x - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;

        [Fact]
        public async Task Parity_DigitalIO_ReadWrite()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();
            var busIO = CreateIOBusUsingSimActions(master, sim);

            // Seed DO word
            await sim.WriteSingleRegisterAsync(0, PD_IN_START + 9, 0);
            await busLinear.WriteDigitalOutputsAsync(new Dictionary<ushort, bool> { [1] = true, [3] = true, [5] = true });
            await busIO.WriteDigitalOutputsAsync(new Dictionary<ushort, bool> { [2] = true, [4] = true });

            var l_out = await busLinear.ReadDigitalOutputsAsync(new ushort[] { 1, 2, 3, 4, 5 });
            var i_out = await busIO.ReadDigitalOutputsAsync(new ushort[] { 1, 2, 3, 4, 5 });

            // Align states by writing same pattern through both and comparing bit-by-bit
            Assert.Equal(l_out.OrderBy(k => k.Key).ToList(), i_out.OrderBy(k => k.Key).ToList());

            // Seed DI word and compare reads
            await sim.WriteSingleRegisterAsync(0, PD_IN_START + 0, 0b_0000_0000_0010_0101);
            var l_in = await busLinear.ReadDigitalInputsAsync(new ushort[] { 1, 2, 5 });
            var i_in = await busIO.ReadDigitalInputsAsync(new ushort[] { 1, 2, 5 });
            Assert.Equal(l_in.OrderBy(k => k.Key).ToList(), i_in.OrderBy(k => k.Key).ToList());
        }

        [Fact]
        public async Task Parity_AnalogInputs_Read()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();
            var busIO = CreateIOBusUsingSimActions(master, sim);

            // Seed raw IB IL words at 8001..8004
            await sim.WriteSingleRegisterAsync(0, PD_IN_START + 1, (ushort)(0.25 * 32512) & 0xFFF8);
            await sim.WriteSingleRegisterAsync(0, PD_IN_START + 2, (ushort)(0.50 * 32512) & 0xFFF8);
            await sim.WriteSingleRegisterAsync(0, PD_IN_START + 3, (ushort)(0.75 * 32512) & 0xFFF8);
            await sim.WriteSingleRegisterAsync(0, PD_IN_START + 4, (ushort)(1.00 * 32512) & 0xFFF8);

            var idxs = new ushort[] { 1, 2, 3, 4 };
            var l_vals = await busLinear.ReadAnalogInputsAsync(idxs);
            var i_vals = await busIO.ReadAnalogInputsAsync(idxs);

            foreach (var k in idxs)
                Assert.InRange(Math.Abs(l_vals[k] - i_vals[k]), 0, 1e-9);
        }

        [Fact]
        public async Task Parity_AnalogOutputs_WriteAndReadback()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();
            var busIO = CreateIOBusUsingSimActions(master, sim);

            var values = new Dictionary<ushort, double> { [1] = 3.3, [2] = 7.7 };
            await busLinear.WriteAnalogOutputsAsync(values);
            await busIO.WriteAnalogOutputsAsync(values);

            var idxs = new ushort[] { 1, 2 };
            var l_vals = await busLinear.ReadAnalogOutputsAsync(idxs);
            var i_vals = await busIO.ReadAnalogOutputsAsync(idxs);

            foreach (var k in idxs)
                Assert.InRange(Math.Abs(l_vals[k] - i_vals[k]), 0, 1e-6);
        }

        [Fact]
        public async Task Parity_SinglePointHelpers()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();
            var busIO = CreateIOBusUsingSimActions(master, sim);

            sim.SeedInputRegister(0, (ushort)(PD_IN_START + 0), 0b_0000_0000_0000_1000);
            Assert.True(await busLinear.ReadDigitalInputAsync(4));
            Assert.True(await busIO.ReadDigitalInputAsync(4));

            await busLinear.WriteDigitalOutputAsync(6, true);
            await busIO.WriteDigitalOutputAsync(6, true);
            var outWord = (await sim.ReadInputRegistersAsync(0, PD_IN_START + 9, 1))[0];
            Assert.True(((outWord >> 5) & 1) == 1);

            await busLinear.WriteAnalogOutputAsync(1, 5.0);
            await busIO.WriteAnalogOutputAsync(1, 5.0);
            var l_single = await busLinear.ReadAnalogOutputsAsync(new ushort[] { 1 });
            var i_single = await busIO.ReadAnalogOutputsAsync(new ushort[] { 1 });
            Assert.InRange(Math.Abs(l_single[1] - i_single[1]), 0, 1e-6);
        }

        [Fact]
        public async Task Parity_IsConnected()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();

            var busIO = CreateIOBusUsingSimActions(master, sim);

            Assert.True(await busLinear.IsConnectedAsync());
            Assert.True(await busIO.IsConnectedAsync());
        }

        [Fact]
        public async Task Parity_SingleOutputRead_Helper()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();
            var busIO = CreateIOBusUsingSimActions(master, sim);

            await busLinear.WriteDigitalOutputAsync(8, true);
            await busIO.WriteDigitalOutputAsync(8, true);

            var l = await busLinear.ReadDigitalOutputAsync(8);
            var i = await busIO.ReadDigitalOutputAsync(8);
            Assert.Equal(i, l);
        }

        [Fact]
        public async Task Parity_Randomized_DigitalIO()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();
            var busIO = CreateIOBusUsingSimActions(master, sim);

            var rng = new Random(42);
            for (int iter = 0; iter < 50; iter++)
            {
                // write a random pattern for 16 DOs
                var map = new Dictionary<ushort, bool>();
                for (ushort idx = 1; idx <= 16; idx++)
                {
                    bool v = rng.NextDouble() > 0.5;
                    map[idx] = v;
                }
                await busLinear.WriteDigitalOutputsAsync(map);
                await busIO.WriteDigitalOutputsAsync(map);

                var indexes = Enumerable.Range(1, 16).Select(i => (ushort)i).ToArray();
                var l = await busLinear.ReadDigitalOutputsAsync(indexes);
                var i = await busIO.ReadDigitalOutputsAsync(indexes);
                Assert.Equal(l.OrderBy(k => k.Key).ToList(), i.OrderBy(k => k.Key).ToList());
            }
        }

        [Fact]
        public async Task Parity_Randomized_AnalogIO()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();
            var busIO = CreateIOBusUsingSimActions(master, sim);

            var rng = new Random(7);
            for (int iter = 0; iter < 50; iter++)
            {
                var values = new Dictionary<ushort, double>
                {
                    [1] = rng.NextDouble() * 10.0,
                    [2] = rng.NextDouble() * 10.0
                };
                await busLinear.WriteAnalogOutputsAsync(values);
                await busIO.WriteAnalogOutputsAsync(values);

                var idxs = new ushort[] { 1, 2 };
                var l_vals = await busLinear.ReadAnalogOutputsAsync(idxs);
                var i_vals = await busIO.ReadAnalogOutputsAsync(idxs);
                foreach (var k in idxs)
                    Assert.InRange(Math.Abs(l_vals[k] - i_vals[k]), 0, 1e-6);
            }
        }

        [Fact]
        public async Task Parity_OutOfRange_Indexes_Throw()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();
            var busIO = CreateIOBusUsingSimActions(master, sim);

            // DI out-of-range
            await Assert.ThrowsAnyAsync<Exception>(async () => await busLinear.ReadDigitalInputAsync(17));
            await Assert.ThrowsAnyAsync<Exception>(async () => await busIO.ReadDigitalInputAsync(17));

            // DO out-of-range
            await Assert.ThrowsAnyAsync<Exception>(async () => await busLinear.ReadDigitalOutputAsync(17));
            await Assert.ThrowsAnyAsync<Exception>(async () => await busIO.ReadDigitalOutputAsync(17));
            await Assert.ThrowsAnyAsync<Exception>(async () => await busLinear.WriteDigitalOutputAsync(17, true));
            await Assert.ThrowsAnyAsync<Exception>(async () => await busIO.WriteDigitalOutputAsync(17, true));

            // AI out-of-range
            await Assert.ThrowsAnyAsync<Exception>(async () => await busLinear.ReadAnalogInputAsync(5));
            await Assert.ThrowsAnyAsync<Exception>(async () => await busIO.ReadAnalogInputsAsync(new ushort[] { 5 }));

            // AO out-of-range
            await Assert.ThrowsAnyAsync<Exception>(async () => await busLinear.WriteAnalogOutputAsync(3, 1.0));
            await Assert.ThrowsAnyAsync<Exception>(async () => await busIO.WriteAnalogOutputAsync(3, 1.0));
        }

        [Fact]
        public async Task Connectivity_Drop_IsConnectedFalse()
        {
            var (master, sim) = CreateMasterBackedBySim();
            var busLinear = CreateLinearBus(master);
            await busLinear.InitializeAsync();

            var busIO = CreateIOBusUsingSimActions(master, sim);

            // Simulate device disappearing: dispose the sim; both should return false.
            sim.Dispose();
            Assert.False(await busLinear.IsConnectedAsync());
            Assert.False(await busIO.IsConnectedAsync());
        }
    }
}


