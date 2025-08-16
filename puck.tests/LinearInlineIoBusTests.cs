using Moq;
using NModbus;
using PhoenixInline.Simple;
using puck.Services.IoBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public class LinearInlineIoBusTests
{
    private const ushort REG_MODULE_COUNT = 1400;
    private const ushort REG_MODULE_INFO = 1401;

    // Matches Inline2701388Tests layout
    private static (Mock<IModbusMaster> master, Dictionary<ushort, ushort> mem) CreateMasterWithLayout()
    {
        var mem = new Dictionary<ushort, ushort>();

        // Module count = 4
        mem[REG_MODULE_COUNT] = 4;

        // Length codes: 1 word DI, 1 word DO, 4 words AI, 4 words AO
        mem[(ushort)(REG_MODULE_INFO + 0)] = (ushort)((0x01 << 8) | 190); // DI
        mem[(ushort)(REG_MODULE_INFO + 1)] = (ushort)((0x01 << 8) | 189); // DO
        mem[(ushort)(REG_MODULE_INFO + 2)] = (ushort)((0x04 << 8) | 127); // AI
        mem[(ushort)(REG_MODULE_INFO + 3)] = (ushort)((0x04 << 8) | 91);  // AO

        var mock = new Mock<IModbusMaster>(MockBehavior.Strict);

        mock.Setup(m => m.ReadInputRegistersAsync(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<ushort>()))
            .Returns<byte, ushort, ushort>((slave, start, count) =>
            {
                var arr = new ushort[count];
                for (int i = 0; i < count; i++)
                {
                    mem.TryGetValue((ushort)(start + i), out var v);
                    arr[i] = v;
                }
                return Task.FromResult(arr);
            });

        mock.Setup(m => m.WriteSingleRegisterAsync(It.IsAny<byte>(), It.IsAny<ushort>(), It.IsAny<ushort>()))
            .Returns<byte, ushort, ushort>((slave, addr, value) =>
            {
                mem[addr] = value;
                // AO config mirror: writes to 8014/8015 reflected at 8005/8006
                if (addr == 8014) mem[8005] = value;
                if (addr == 8015) mem[8006] = value;
                return Task.CompletedTask;
            });

        mock.Setup(m => m.Dispose());

        return (mock, mem);
    }

    private static Func<CancellationToken, Task<IModbusMaster>> MasterFactory(IModbusMaster m)
        => _ => Task.FromResult(m);

    [Fact]
    public async Task Initialize_BuildsMaps_AndAllowsBasicIo()
    {
        var (m, mem) = CreateMasterWithLayout();
        // Seed DI with bit 2 and 4 set, AO meas ch2 to ~10V
        mem[8000] = 0b_0000_0000_0000_1010;
        ushort raw10V = (ushort)((int)Math.Round(32512.0 * (10.0 / 10.837)) & 0xFFF8);
        mem[8008] = raw10V; // AO IN base 8005 + 3 for ch2

        var bus = new LinearInlineIoBus(MasterFactory(m.Object));
        await bus.InitializeAsync();

        var di = await bus.ReadDigitalInputsAsync(new ushort[] { 2 });
        Assert.True(di[2]);

        var ao = await bus.ReadAnalogOutputsAsync(new ushort[] { 2 });
        Assert.InRange(ao[2], 9.5, 10.5);
    }

    [Fact]
    public async Task WriteDigitalOutputs_SetsBitsInDoWord()
    {
        var (m, mem) = CreateMasterWithLayout();
        mem[8009] = 0; // DO word
        var bus = new LinearInlineIoBus(MasterFactory(m.Object));
        await bus.InitializeAsync();

        await bus.WriteDigitalOutputsAsync(new Dictionary<ushort, bool> { [1] = true, [3] = true });
        Assert.Equal(0b_0000_0000_0000_0101, mem[8009]);

        // cache updated
        Assert.True(bus.DigitalOutputState[1]!.Value.State);
        Assert.True(bus.DigitalOutputState[3]!.Value.State);
    }

    [Fact]
    public async Task WriteAnalogOutput_WritesToAoChannelWord()
    {
        var (m, mem) = CreateMasterWithLayout();
        var bus = new LinearInlineIoBus(MasterFactory(m.Object));
        await bus.InitializeAsync();

        await bus.WriteAnalogOutputAsync(2, 5.0);

        // AO OUT base 8014; ch2 at +3 -> 8017
        Assert.True(mem.ContainsKey(8017));
    }

    [Fact]
    public async Task SetAoRange_TriggersConfigWriteForOccurrence()
    {
        var (m, mem) = CreateMasterWithLayout();
        var bus = new LinearInlineIoBus(MasterFactory(m.Object));
        await bus.InitializeAsync();

        await bus.SetAoRangeAsync(1, AnalogRange.mA_4_20);

        // Expect a cfg write at 8014 or 8015 due to reconfigure
        Assert.True(mem.ContainsKey(8014) || mem.ContainsKey(8015));
    }
}


