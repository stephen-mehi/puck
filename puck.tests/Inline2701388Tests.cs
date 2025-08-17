using Moq;
using NModbus;
using PhoenixInline.Simple;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace puck.Services.IoBus;
public class Inline2701388Tests
{
    private const ushort REG_MODULE_COUNT = 1400;
    private const ushort REG_MODULE_INFO = 1401;
    private const ushort PD_IN_START = 8000;

    // Station layout: [DI(1 word), DO(1 word), AI(4 words), AO(4 words)]
    // Derived addresses from NewPhoenix.cs logic:
    // DI In: 8000
    // AI In: 8001..8004, AI Out: 8010..8013 (cfg per channel)
    // AO In: 8005..8008 (meas at +2), AO Out: 8014..8017 (cfg at +0..1, ch at +2..3)
    // DO Out: 8009
    private static (Mock<IModbusMaster> master, Dictionary<ushort, ushort> mem) CreateMasterWithLayout()
    {
        var mem = new Dictionary<ushort, ushort>();

        // Module count = 4
        mem[REG_MODULE_COUNT] = 4;

        // Data-length codes: 1-word for DI/DO (0x01), 4-words for AI/AO (0x04)
        // Low byte is id: DI=190, DO=189, AI=127, AO=91
        mem[(ushort)(REG_MODULE_INFO + 0)] = (ushort)((0x01 << 8) | 190);
        mem[(ushort)(REG_MODULE_INFO + 1)] = (ushort)((0x01 << 8) | 189);
        mem[(ushort)(REG_MODULE_INFO + 2)] = (ushort)((0x04 << 8) | 127);
        mem[(ushort)(REG_MODULE_INFO + 3)] = (ushort)((0x04 << 8) | 91);

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

                // AO config mirror: when writing AO cfg at 8014/8015, update AO IN echo at 8005/8006
                if (addr == 8014) mem[8005] = value; // cfg1 echo
                if (addr == 8015) mem[8006] = value; // cfg2 echo

                return Task.CompletedTask;
            });

        mock.Setup(m => m.Dispose());

        return (mock, mem);
    }

    [Fact]
    public async Task IsConnectedAsync_ReturnsTrue_OnSuccessfulRead()
    {
        var (m, _) = CreateMasterWithLayout();
        var driver = new Inline2701388(m.Object);
        var ok = await driver.IsConnectedAsync();
        Assert.True(ok);
    }

    [Fact]
    public async Task IsConnectedAsync_ReturnsFalse_OnException()
    {
        var mock = new Mock<IModbusMaster>(MockBehavior.Strict);
        mock.Setup(m => m.ReadInputRegistersAsync(It.IsAny<byte>(), 1400, 1))
            .ThrowsAsync(new Exception("fail"));
        mock.Setup(m => m.Dispose());

        var driver = new Inline2701388(mock.Object);
        var ok = await driver.IsConnectedAsync();
        Assert.False(ok);
    }

    [Fact]
    public async Task InitializeAsync_ComputesExpectedAddresses_AndAllowsReadWrite()
    {
        var (m, mem) = CreateMasterWithLayout();
        // Seed some IO state
        mem[8000] = 0b_0000_0000_0000_1010; // DI word (bits 2 and 4 set)
        mem[8009] = 0; // DO out word

        var driver = new Inline2701388(m.Object);
        await driver.InitializeAsync();

        // Read DI word -> should read from 8000
        var di = await driver.ReadDIWordAsync(0);
        Assert.Equal((ushort)0b_0000_0000_0000_1010, di);
        mockVerifyRead(m, 8000, 1);

        // Write DO bit 2 -> writes to 8009
        await driver.WriteDOAsync(true, 0, 2);
        Assert.Equal((ushort)0b_0000_0000_0000_0010, mem[8009]);
        mockVerifyWrite(m, 8009);
    }

    [Fact]
    public async Task ConfigureAI_WritesFourConfigWords()
    {
        var (m, _) = CreateMasterWithLayout();
        var driver = new Inline2701388(m.Object);
        await driver.InitializeAsync();

        await driver.ConfigureAI2700458Async(AnalogRange.mA_4_20);

        // Expect writes to AI OUT base 8010..8013
        for (ushort a = 8010; a <= 8013; a++) mockVerifyWrite(m, a);
    }

    [Fact]
    public async Task ReadAI_MapsRawToMilliamps()
    {
        var (m, mem) = CreateMasterWithLayout();
        // Channel 1 raw = 0 -> expect ~4.0 mA for 4-20 range
        mem[8001] = 0;
        var driver = new Inline2701388(m.Object);
        await driver.InitializeAsync();

        var val = await driver.ReadAI2700458_mAAsync(1, AnalogRange.mA_4_20);
        Assert.InRange(val, 3.99, 4.01);
    }

    [Fact]
    public async Task ConfigureAO_MirrorSucceeds()
    {
        var (m, _) = CreateMasterWithLayout();
        var driver = new Inline2701388(m.Object);
        await driver.InitializeAsync();

        await driver.ConfigureAO2700775Async(AnalogRange.V_0_10, AnalogRange.mA_4_20);

        // Writes must occur to 8014 and 8015
        mockVerifyWrite(m, 8014);
        mockVerifyWrite(m, 8015);
    }

    [Fact]
    public async Task WriteAO_WritesMaskedValue_ToCorrectAddress()
    {
        var (m, _) = CreateMasterWithLayout();
        var driver = new Inline2701388(m.Object);
        await driver.InitializeAsync();

        await driver.WriteAO2700775Async(2, 5.0, AnalogRange.V_0_10);

        m.Verify(mm => mm.WriteSingleRegisterAsync(0, 8017, It.Is<ushort>(v => (v & 0x000F) == 0)), Times.Once);
    }

    [Fact]
    public async Task ReadAO_ReadsMeasurementAndMaps()
    {
        var (m, mem) = CreateMasterWithLayout();
        var driver = new Inline2701388(m.Object);
        await driver.InitializeAsync();

        // Set measurement for ch2 at AO IN base 8005 + 2 + (2-1) = 8008
        // Choose raw corresponding to ~10.0 V
        ushort raw = (ushort)((int)Math.Round(32512.0 * (10.0 / 10.837)) & 0xFFF8);
        mem[8008] = raw;

        var v = await driver.ReadAO2700775Async(2, AnalogRange.V_0_10);
        Assert.InRange(v, 9.5, 10.5);
    }

    private static void mockVerifyRead(Mock<IModbusMaster> m, ushort start, ushort count)
    {
        m.Verify(mm => mm.ReadInputRegistersAsync(0, start, count), Times.AtLeastOnce);
    }

    private static void mockVerifyWrite(Mock<IModbusMaster> m, ushort addr)
    {
        m.Verify(mm => mm.WriteSingleRegisterAsync(0, addr, It.IsAny<ushort>()), Times.AtLeastOnce);
    }
}


