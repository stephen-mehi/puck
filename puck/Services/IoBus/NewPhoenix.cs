// PhoenixInline2701388Simple.cs
// Minimal helpers for IL ETH BK DI8 DO4 2TX-XC-PAC + common Inline analog I/O
// Requires NModbus (IModbusMaster)

using NModbus;
using puck.Services.IoBus;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data.Common;
using System.Linq;
using System.Net.Sockets;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using System.Threading.Tasks;

namespace PhoenixInline.Simple
{
    public enum AnalogRange
    {
        // Supported by AI 2700458 + AO 2700775 per data sheets
        mA_0_20,
        mA_4_20,
        V_0_10
    }

    public sealed class Inline2701388 : IDisposable
    {
        private bool _isDisposed;

        // ---- PAC special registers (unit id 0) ----
        // 1400 -> module count; 1401.. -> module info words (hi=data-length code, lo=id code)
        // Process data starts at IN=8000 (words). OUT words come after IN words.
        private const ushort REG_MODULE_COUNT = 1400;
        private const ushort REG_MODULE_INFO = 1401;
        private const ushort PD_IN_START = 8000;

        // Known module id codes we implement
        private const byte ID_DI = 190;               // digital input
        private const byte ID_DO = 189;               // digital output
        private const byte ID_AI_2700458 = 127;       // IB IL AI 4/I-PAC
        private const byte ID_AO_2700775 = 91;        // IB IL AO 2/UI-PAC

        private readonly IModbusMaster _master;
        private readonly byte _unitId;

        // Simplified module record
        private sealed class Mod
        {
            public ushort Index;           // physical module index in station (0..)
            public byte IdCode;            // Phoenix id code (low byte of module info)
            public ushort WordCount;       // process-data words (16-bit words) reserved per direction
            public ushort InStart;         // IN word address (0 if none)
            public ushort OutStart;        // OUT word address (0 if none)
        }

        private List<Mod> _mods = new();

        public Inline2701388(IModbusMaster master, byte unitId = 0)
        {
            _master = master ?? throw new ArgumentNullException(nameof(master));
            _unitId = unitId;
        }

        public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
        {
            //READ SPECIFIC REGISTER HERE
            try
            {
                if (_master == null)
                    return false;

                await _master.ReadInputRegistersAsync(0, 1400, 1);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---------- Station discovery & address assignment ----------

        public async Task InitializeAsync(CancellationToken ct = default)
        {
            // Wait until module count shows up (PAC boots before Inline scan completes)
            ushort count = 0;
            for (var i = 0; i < 20 && count == 0; i++)
            {
                count = (await _master.ReadInputRegistersAsync(_unitId, REG_MODULE_COUNT, 1).ConfigureAwait(false))[0];
                if (count == 0) await Task.Delay(250, ct).ConfigureAwait(false);
            }
            if (count == 0) throw new InvalidOperationException("No Inline modules detected by the PAC.");

            // Read module info words
            var infos = await _master.ReadInputRegistersAsync(_unitId, REG_MODULE_INFO, count).ConfigureAwait(false);

            // Build preliminary list with word counts (from data-length code)
            _mods = infos.Select((w, idx) => new Mod
            {
                Index = (ushort)idx,
                IdCode = (byte)(w & 0x00FF),
                WordCount = (ushort)CeilDiv(DecodeBits((byte)(w >> 8)), 16)
            }).ToList();

            // Assign IN addresses first (modules with inputs or both)
            ushort inPtr = PD_IN_START;
            foreach (var m in _mods)
            {
                if (HasInputs(m.IdCode))
                {
                    m.InStart = inPtr;
                    inPtr += m.WordCount;
                }
            }

            // Then OUT addresses
            ushort outPtr = inPtr;
            foreach (var m in _mods)
            {
                if (HasOutputs(m.IdCode))
                {
                    m.OutStart = outPtr;
                    outPtr += m.WordCount;
                }
            }
        }

        // ---- Public helpers to find modules by id code (first occurrence) ----
        private Mod GetFirst(byte id) =>
            _mods.FirstOrDefault(m => m.IdCode == id)
            ?? throw new KeyNotFoundException($"Module with id {id} not present on station.");

        // ---- Digital I/O (single word per module assumed; LSB-first bit ordering) ----

        public async Task<bool> ReadDOAsync(int moduleOccurrence = 0, int bit1Based = 1, CancellationToken ct = default)
        {
            var mod = _mods.Where(m => m.IdCode == ID_DO).Skip(moduleOccurrence).FirstOrDefault()
                      ?? throw new KeyNotFoundException("No DO module found.");
            if (bit1Based is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(bit1Based));
            var val = (await _master.ReadInputRegistersAsync(_unitId, mod.OutStart, 1).ConfigureAwait(false))[0];
            return ((val >> (bit1Based - 1)) & 1) == 1;
        }


        public async Task<bool> ReadDIAsync(int moduleOccurrence = 0, int bit1Based = 1, CancellationToken ct = default)
        {
            var mod = _mods.Where(m => m.IdCode == ID_DI).Skip(moduleOccurrence).FirstOrDefault()
                      ?? throw new KeyNotFoundException("No DI module found.");
            if (bit1Based is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(bit1Based));
            var val = (await _master.ReadInputRegistersAsync(_unitId, mod.InStart, 1).ConfigureAwait(false))[0];
            return ((val >> (bit1Based - 1)) & 1) == 1;
        }

        public async Task WriteDOAsync(bool state, int moduleOccurrence = 0, int bit1Based = 1, CancellationToken ct = default)
        {
            var mod = _mods.Where(m => m.IdCode == ID_DO).Skip(moduleOccurrence).FirstOrDefault()
                      ?? throw new KeyNotFoundException("No DO module found.");
            if (bit1Based is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(bit1Based));
            var reg = (await _master.ReadInputRegistersAsync(_unitId, mod.OutStart, 1).ConfigureAwait(false))[0];
            ushort updated = state
                ? (ushort)(reg | (1 << (bit1Based - 1)))
                : (ushort)(reg & ~(1 << (bit1Based - 1)));
            await _master.WriteSingleRegisterAsync(_unitId, mod.OutStart, updated).ConfigureAwait(false);
        }

        // ---- Analog Input: IB IL AI 4/I-PAC (2700458) ----

        // Optional: configure channel range + averaging (greatly simplified)
        public async Task ConfigureAI2700458Async(
            AnalogRange rangePerChannel = AnalogRange.mA_4_20,
            CancellationToken ct = default)
        {
            var ai = GetFirst(ID_AI_2700458);
            // This module accepts configuration via OUT words (one per channel).
            // We only expose range + simple averaging=4. See doc’s IB IL format bits.
            ushort BuildCfg(AnalogRange r)
            {
                // bit15 = 1 (apply), averaging=4 (0b10 in bits 9..8), range bits in low nibble.
                int v = 0b_1000_0000_0000_0000;
                // avg=4
                v |= 0b_0000_0010_0000_0000;
                v |= r switch
                {
                    AnalogRange.mA_0_20 => 0b_0000_0000_0000_0100,
                    AnalogRange.mA_4_20 => 0b_0000_0000_0000_0110,
                    _ => throw new NotSupportedException("AI 2700458 supports 0–20 mA / 4–20 mA.")
                };
                return (ushort)v;
            }

            var cfg = BuildCfg(rangePerChannel);
            for (ushort ch = 0; ch < 4; ch++)
            {
                await _master.WriteSingleRegisterAsync(_unitId, (ushort)(ai.OutStart + ch), cfg).ConfigureAwait(false);
            }
        }

        public async Task<double> ReadAI2700458_mAAsync(int channel1Based, AnalogRange range = AnalogRange.mA_4_20, CancellationToken ct = default)
        {
            if (channel1Based is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(channel1Based));
            var ai = GetFirst(ID_AI_2700458);
            var reg = (await _master.ReadInputRegistersAsync(_unitId, (ushort)(ai.InStart + (channel1Based - 1)), 1).ConfigureAwait(false))[0];
            var raw = DecodeIBIL_SignedLeftJustified12b(reg); // 0..32512 (with sign, masked)
            return range switch
            {
                AnalogRange.mA_0_20 => Map(raw, 0, 32512, 0.0, 21.675),  // per datasheet
                AnalogRange.mA_4_20 => Map(raw, 0, 32512, 4.0, 21.339),
                _ => throw new NotSupportedException("AI 2700458 only supports current ranges.")
            };
        }

        // ---- Analog Output: IB IL AO 2/UI-PAC (2700775) ----

        public async Task ConfigureAO2700775Async(
            AnalogRange ch1, AnalogRange ch2,
            CancellationToken ct = default)
        {
            var ao = GetFirst(ID_AO_2700775);

            async Task Mirror(ushort value, ushort inAddr, ushort outAddr, TimeSpan timeout)
            {
                // Compare masked echo (lower 6 bits) until equal or timeout
                ushort mask = 0b_0000_0000_0011_1111;
                var start = DateTime.UtcNow;
                while (true)
                {
                    await _master.WriteSingleRegisterAsync(_unitId, outAddr, value).ConfigureAwait(false);
                    var read = (await _master.ReadInputRegistersAsync(_unitId, inAddr, 1).ConfigureAwait(false))[0];
                    if ((read & mask) == (value & mask)) return;
                    if (DateTime.UtcNow - start > timeout)
                        throw new TimeoutException("AO config mirror timed out.");
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }
            }

            static ushort BuildCfg(AnalogRange r)
            {
                // bit15=1 apply; low nibble range code (0x0A => 4..20 mA)
                int v = 0b_1000_0000_0000_0000;
                v |= r switch
                {
                    AnalogRange.mA_4_20 => 0b_0000_0000_0000_1010,
                    AnalogRange.V_0_10 => 0, // 0-10 V is default code 0
                    _ => throw new NotSupportedException("AO supports 0–10 V or 4–20 mA.")
                };
                return (ushort)v;
            }

            // two config words: OUT[0], OUT[1]; echoes appear in IN[0], IN[1]
            await Mirror(BuildCfg(ch1), (ushort)(ao.InStart + 0), (ushort)(ao.OutStart + 0), TimeSpan.FromSeconds(2));
            await Mirror(BuildCfg(ch2), (ushort)(ao.InStart + 1), (ushort)(ao.OutStart + 1), TimeSpan.FromSeconds(2));
        }

        public async Task WriteAO2700775Async(int channel1Based, double value, AnalogRange range, CancellationToken ct = default)
        {
            if (channel1Based is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channel1Based));
            var ao = GetFirst(ID_AO_2700775);

            ushort ToReg(double v, AnalogRange r)
            {
                // IB IL AO format: 12-bit (with sign) left-justified -> mask to keep 0x7FF0
                double raw = r switch
                {
                    AnalogRange.V_0_10 => Map(Clamp(v, 0, 10), 0.0, 10.837, 0.0, 32512.0),
                    AnalogRange.mA_4_20 => Map(Clamp(v, 4, 20), 4.0, 21.339, 0.0, 32512.0),
                    _ => throw new NotSupportedException("AO supports 0–10 V or 4–20 mA.")
                };
                return (ushort)(((int)Math.Round(raw)) & 0b_0111_1111_1111_0000);
            }

            // OUT words: [0]=cfg1, [1]=cfg2, [2]=ch1, [3]=ch2
            await _master.WriteSingleRegisterAsync(
                _unitId,
                (ushort)(ao.OutStart + 2 + (channel1Based - 1)),
                ToReg(value, range)
            ).ConfigureAwait(false);
        }

        public async Task<double> ReadAO2700775Async(int channel1Based, AnalogRange range, CancellationToken ct = default)
        {
            if (channel1Based is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channel1Based));
            var ao = GetFirst(ID_AO_2700775);
            // IN words: [0]=echo cfg1, [1]=echo cfg2, [2]=meas1, [3]=meas2
            var reg = (await _master.ReadInputRegistersAsync(_unitId, (ushort)(ao.InStart + 2 + (channel1Based - 1)), 1).ConfigureAwait(false))[0];
            var raw = DecodeIBIL_SignedLeftJustified12b(reg);
            return range switch
            {
                AnalogRange.V_0_10 => Map(raw, 0, 32512, 0.0, 10.837),
                AnalogRange.mA_4_20 => Map(raw, 0, 32512, 4.0, 21.339),
                _ => throw new NotSupportedException()
            };
        }

        // ------- Batch helpers (non-invasive; keep driver simple) -------

        public async Task<ushort> ReadDIWordAsync(int moduleOccurrence = 0, CancellationToken ct = default)
        {
            var mod = _mods.Where(m => m.IdCode == ID_DI).Skip(moduleOccurrence).FirstOrDefault()
                      ?? throw new KeyNotFoundException("No DI module found.");
            var val = (await _master.ReadInputRegistersAsync(_unitId, mod.InStart, 1).ConfigureAwait(false))[0];
            return val;
        }

        public async Task<ushort> ReadDOWordAsync(int moduleOccurrence = 0, CancellationToken ct = default)
        {
            var mod = _mods.Where(m => m.IdCode == ID_DO).Skip(moduleOccurrence).FirstOrDefault()
                      ?? throw new KeyNotFoundException("No DO module found.");
            var val = (await _master.ReadInputRegistersAsync(_unitId, mod.OutStart, 1).ConfigureAwait(false))[0];
            return val;
        }

        public async Task WriteDOWordAsync(int moduleOccurrence, ushort value, CancellationToken ct = default)
        {
            var mod = _mods.Where(m => m.IdCode == ID_DO).Skip(moduleOccurrence).FirstOrDefault()
                      ?? throw new KeyNotFoundException("No DO module found.");
            await _master.WriteSingleRegisterAsync(_unitId, mod.OutStart, value).ConfigureAwait(false);
        }

        // AI 2700458: read N channels (1-based), contiguous
        public async Task<ushort[]> ReadAI2700458_RangeAsync(int moduleOccurrence, int startChannel1Based, int count, CancellationToken ct = default)
        {
            if (startChannel1Based < 1 || count < 1 || startChannel1Based + count - 1 > 4)
                throw new ArgumentOutOfRangeException(nameof(startChannel1Based));
            var ai = _mods.Where(m => m.IdCode == ID_AI_2700458).Skip(moduleOccurrence).FirstOrDefault()
                     ?? throw new KeyNotFoundException("AI 2700458 module not found.");
            var addr = (ushort)(ai.InStart + (startChannel1Based - 1));
            return await _master.ReadInputRegistersAsync(_unitId, addr, (ushort)count).ConfigureAwait(false);
        }

        // AO 2700775: read N channels (1-based), contiguous (measurement words at IN + 2)
        public async Task<ushort[]> ReadAO2700775_RangeAsync(int moduleOccurrence, int startChannel1Based, int count, CancellationToken ct = default)
        {
            if (startChannel1Based < 1 || count < 1 || startChannel1Based + count - 1 > 2)
                throw new ArgumentOutOfRangeException(nameof(startChannel1Based));
            var ao = _mods.Where(m => m.IdCode == ID_AO_2700775).Skip(moduleOccurrence).FirstOrDefault()
                     ?? throw new KeyNotFoundException("AO 2700775 module not found.");
            var addr = (ushort)(ao.InStart + 2 + (startChannel1Based - 1));
            return await _master.ReadInputRegistersAsync(_unitId, addr, (ushort)count).ConfigureAwait(false);
        }


        // ---------- Utilities ----------

        private static int CeilDiv(int a, int b) => (a + b - 1) / b;

        private static int DecodeBits(byte lengthCode)
        {
            // High 2 bits select unit, low 6 bits the count; units: 0=Word(16), 2=Byte(8), 1=Nibble(4), 3=Bit(1)
            int unitId = (lengthCode >> 6) & 0x3;
            int units = lengthCode & 0x3F;
            int unitBits = unitId switch { 0 => 16, 2 => 8, 1 => 4, 3 => 1, _ => 16 };
            return units * unitBits;
        }

        private static bool HasInputs(byte id) =>
            id == ID_DI || id == ID_AI_2700458 || id == ID_AO_2700775;

        private static bool HasOutputs(byte id) =>
            id == ID_DO || id == ID_AO_2700775 || id == ID_AI_2700458; // AI uses OUT for config

        private static ushort DecodeIBIL_SignedLeftJustified12b(ushort reg)
        {
            // IB IL analog: sign in bit15, 12-bit magnitude left-justified (bits 14..4). We return |value| left-justified (0..32512).
            bool neg = (reg >> 15) == 1;
            ushort mag = (ushort)(reg & 0b_0111_1111_1111_1000); // keep top 12 bits aligned to bit4..15
            // For our ranges we only map the magnitude (neg not expected for 0.. or 4.. ranges).
            // If needed, a true signed conversion could be applied here.
            return neg ? (ushort)(-((short)mag)) : mag;
        }

        private static double Map(double x, double inMin, double inMax, double outMin, double outMax)
        {
            return (x - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;
        }

        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);


        #region IDisposable

        private void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _master?.Dispose();
            }

            _isDisposed = true;
        }

        public void Dispose()
        {
            // Dispose of unmanaged resources.
            Dispose(true);
            // Suppress finalization.
            GC.SuppressFinalize(this);
        }

        #endregion

    }


    // LinearInlineIoBusRx.cs
    // IIOBus + IPhoenixProxy with linear 1-based DI/DO/AI/AO, Rx connectivity, and robust reconnection.
    //
    // Assumes a small "Inline2701388" driver with helpers:
    //   - Task<ushort> ReadDIWordAsync(int occ, CancellationToken)
    //   - Task<ushort> ReadDOWordAsync(int occ, CancellationToken)
    //   - Task WriteDOWordAsync(int occ, ushort word, CancellationToken)
    //   - Task ConfigureAI2700458Async(AnalogRange range, CancellationToken)
    //   - Task<ushort[]> ReadAI2700458_RangeAsync(int occ, int startCh1Based, int len, CancellationToken)
    //   - Task ConfigureAO2700775Async(AnalogRange ch1Range, AnalogRange ch2Range, CancellationToken)
    //   - Task<ushort[]> ReadAO2700775_RangeAsync(int occ, int startCh1Based, int len, CancellationToken)
    //   - Task WriteAO2700775Async(int ch1Based, double value, AnalogRange range, CancellationToken)
    //
    // Your value types (as provided):
    // public readonly struct DigitalIoState { public bool State { get; } public DateTime TimeStamp { get; } ... }
    // public readonly struct AnalogIoState  { public double State { get; } public DateTime TimeStamp { get; } ... }

    public sealed class LinearInlineIoBus : IIOBus, IPhoenixProxy, IDisposable
    {
        // ---- Connection plumbing ----
        private readonly Func<CancellationToken, Task<IModbusMaster>> _masterFactory;
        private readonly TimeSpan _watchdogInterval;
        private readonly Random _rng = new();
        private readonly SemaphoreSlim _reconnectLock = new(1, 1);

        private IModbusMaster? _master;
        private Inline2701388? _pac;

        private IDisposable? _watchdogSub;
        private TimeSpan _backoff = TimeSpan.Zero;
        private static readonly TimeSpan BackoffMin = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan BackoffMax = TimeSpan.FromSeconds(10);

        // Rx connectivity
        private readonly BehaviorSubject<bool> _isConnected = new(false);
        public IObservable<bool> IsConnected => _isConnected.AsObservable();
        public bool IsCurrentlyConnected => _isConnected.Value;

        // ---- Linear maps: linear index -> (occurrence, bit/channel)
        private readonly List<(int occ, int leaf)> _diMap = new(); // 16 bits/module
        private readonly List<(int occ, int leaf)> _doMap = new(); // 16 bits/module
        private readonly List<(int occ, int leaf)> _aiMap = new(); // 4 ch/module (2700458)
        private readonly List<(int occ, int leaf)> _aoMap = new(); // 2 ch/module (2700775)

        // ---- Per-linear-index ranges
        private readonly Dictionary<ushort, AnalogRange> _aiRanges = new();
        private readonly Dictionary<ushort, AnalogRange> _aoRanges = new();
        private AnalogRange _defaultAiRange = AnalogRange.mA_4_20;
        private (AnalogRange ch1, AnalogRange ch2) _defaultAoRanges = (AnalogRange.V_0_10, AnalogRange.V_0_10);

        // ---- State caches (nullable == unknown)
        private readonly object _stateGate = new();
        private readonly Dictionary<ushort, DigitalIoState?> _diState = new();
        private readonly Dictionary<ushort, DigitalIoState?> _doState = new();
        private readonly Dictionary<ushort, AnalogIoState?> _aiState = new();
        private readonly Dictionary<ushort, AnalogIoState?> _aoState = new();

        // ---- Change streams (optional; use if you want push instead of pull) ----
        private readonly Subject<(ushort index, DigitalIoState state)> _diChanges = new();
        private readonly Subject<(ushort index, DigitalIoState state)> _doChanges = new();
        private readonly Subject<(ushort index, AnalogIoState state)> _aiChanges = new();
        private readonly Subject<(ushort index, AnalogIoState state)> _aoChanges = new();


        public LinearInlineIoBus(
            Func<CancellationToken, Task<IModbusMaster>> masterFactory,
            TimeSpan? watchdogInterval = null)
        {
            _masterFactory = masterFactory ?? throw new ArgumentNullException(nameof(masterFactory));
            _watchdogInterval = watchdogInterval ?? TimeSpan.Zero;
        }

        // ------------------------- Lifecycle -------------------------

        public async Task InitializeAsync(
            AnalogRange defaultAiRange = AnalogRange.mA_4_20,
            (AnalogRange ch1, AnalogRange ch2)? defaultAoRanges = null,
            CancellationToken ct = default)
        {
            _defaultAiRange = defaultAiRange;
            _defaultAoRanges = defaultAoRanges ?? (AnalogRange.V_0_10, AnalogRange.V_0_10);

            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await BuildMapsAsync(ct).ConfigureAwait(false);

            // Rx watchdog (optional)
            _watchdogSub?.Dispose();
            if (_watchdogInterval > TimeSpan.Zero)
            {
                _watchdogSub =
                    Observable
                        .Interval(_watchdogInterval)
                        .SelectMany(_ => Observable.FromAsync(_ => EnsureConnectedAsync(CancellationToken.None)))
                        .Subscribe(_ => { }, _ => { /* swallow */ });
            }
        }

        // ===== 1) New fields (add inside LinearInlineIoBus) =====

        // Miss -> null policy and analog deadband (default values; can be overridden per StartScanning)
        private int _missThresholdToNull = 5;
        private double _analogDeadband = 1e-3;

        // Per-index consecutive-miss counters
        private readonly Dictionary<ushort, int> _diMiss = new();
        private readonly Dictionary<ushort, int> _doMiss = new();
        private readonly Dictionary<ushort, int> _aiMiss = new();
        private readonly Dictionary<ushort, int> _aoMiss = new();

        // Optional change streams were already declared earlier:
        // _diChanges, _doChanges, _aiChanges, _aoChanges

        // ===== 2) ScanOptions with new knobs (replace old record) =====

        public sealed record ScanOptions(
            TimeSpan? DigitalInputsPeriod = null,
            TimeSpan? DigitalOutputsPeriod = null,
            TimeSpan? AnalogInputsPeriod = null,
            TimeSpan? AnalogOutputsPeriod = null,
            bool PushChangeEvents = true,
            bool WarmReadOutputs = false, // also poll DO/AO readback
            TimeSpan? InitialJitterMax = null,
            int MissThresholdToNull = 5,     // after N consecutive scan misses -> null
            double AnalogDeadband = 1e-3   // change threshold for AI/AO (engineering units)
        );

        // Keep a copy of active options for loops to read:
        private ScanOptions _activeScanOptions = new();
        private readonly CompositeDisposable _scanDisposables = new();

        // ===== 3) StartScanning/StopScanning (replace the old StartScanning & StopScanning) =====

        public IDisposable StartScanning(ScanOptions? options = null)
        {
            StopScanning(); // idempotent
            _activeScanOptions = options ?? new ScanOptions();
            _missThresholdToNull = Math.Max(1, _activeScanOptions.MissThresholdToNull);
            _analogDeadband = Math.Max(0.0, _activeScanOptions.AnalogDeadband);

            var diPeriod = _activeScanOptions.DigitalInputsPeriod ?? TimeSpan.FromMilliseconds(25);
            var doPeriod = _activeScanOptions.DigitalOutputsPeriod ?? TimeSpan.FromMilliseconds(100);
            var aiPeriod = _activeScanOptions.AnalogInputsPeriod ?? TimeSpan.FromMilliseconds(50);
            var aoPeriod = _activeScanOptions.AnalogOutputsPeriod ?? TimeSpan.FromMilliseconds(200);
            var jitterMax = _activeScanOptions.InitialJitterMax ?? TimeSpan.FromMilliseconds(150);

            // DI
            _scanDisposables.Add(
                IsConnected
                    .DistinctUntilChanged()
                    .Select(isUp => isUp
                        ? Observable.Timer(RandomJitter(jitterMax), diPeriod)
                        : Observable.Empty<long>())
                    .Switch()
                    .SelectMany(_ => Observable.FromAsync(ct => ScanDIAsync(_activeScanOptions.PushChangeEvents, ct)))
                    .Subscribe(_ => { }, _ => { })
            );

            // DO (optional warm readback)
            if (_activeScanOptions.WarmReadOutputs && _doMap.Count > 0)
            {
                _scanDisposables.Add(
                    IsConnected
                        .DistinctUntilChanged()
                        .Select(isUp => isUp
                            ? Observable.Timer(RandomJitter(jitterMax), doPeriod)
                            : Observable.Empty<long>())
                        .Switch()
                        .SelectMany(_ => Observable.FromAsync(ct => ScanDOAsync(_activeScanOptions.PushChangeEvents, ct)))
                        .Subscribe(_ => { }, _ => { })
                );
            }

            // AI
            if (_aiMap.Count > 0)
            {
                _scanDisposables.Add(
                    IsConnected
                        .DistinctUntilChanged()
                        .Select(isUp => isUp
                            ? Observable.Timer(RandomJitter(jitterMax), aiPeriod)
                            : Observable.Empty<long>())
                        .Switch()
                        .SelectMany(_ => Observable.FromAsync(ct => ScanAIAsync(_activeScanOptions.PushChangeEvents, ct)))
                        .Subscribe(_ => { }, _ => { })
                );
            }

            // AO readback (optional)
            if (_activeScanOptions.WarmReadOutputs && _aoMap.Count > 0)
            {
                _scanDisposables.Add(
                    IsConnected
                        .DistinctUntilChanged()
                        .Select(isUp => isUp
                            ? Observable.Timer(RandomJitter(jitterMax), aoPeriod)
                            : Observable.Empty<long>())
                        .Switch()
                        .SelectMany(_ => Observable.FromAsync(ct => ScanAOAsync(_activeScanOptions.PushChangeEvents, ct)))
                        .Subscribe(_ => { }, _ => { })
                );
            }

            return Disposable.Create(StopScanning);

            TimeSpan RandomJitter(TimeSpan max) =>
                max <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(_rng.Next(0, (int)max.TotalMilliseconds));
        }

        public void StopScanning() => _scanDisposables.Clear();

        // ===== 4) Null caches on disconnect (augment PublishConnection or EnsureConnected failure path) =====

        // Call this when connectivity goes false:
        private void ClearCachesToNull()
        {
            lock (_stateGate)
            {
                foreach (var k in _diState.Keys.ToList()) _diState[k] = null;
                foreach (var k in _doState.Keys.ToList()) _doState[k] = null;
                foreach (var k in _aiState.Keys.ToList()) _aiState[k] = null;
                foreach (var k in _aoState.Keys.ToList()) _aoState[k] = null;
                _diMiss.Clear(); _doMiss.Clear(); _aiMiss.Clear(); _aoMiss.Clear();
            }
        }

        // Ensure you invoke ClearCachesToNull() when switching to false:
        private void PublishConnection(bool connected)
        {
            if (!_isConnected.IsDisposed && _isConnected.Value != connected)
            {
                _isConnected.OnNext(connected);
                if (!connected) ClearCachesToNull();
            }
        }

        // ===== 5) Helpers: miss/not-ok + analog compare =====

        private void NoteOk<T>(
            Dictionary<ushort, T?> cache,
            Dictionary<ushort, int> miss,
            ushort lin,
            T value,
            bool push,
            Func<T, T, bool> equals,
            IObserver<(ushort, T)>? changes = null) where T : struct
        {
            miss[lin] = 0;

            var prev = cache.TryGetValue(lin, out var p) ? p : (T?)null;
            cache[lin] = value;

            if (push && (!prev.HasValue || !equals(prev.Value, value)))
                changes?.OnNext((lin, value));
        }


        private void NoteMiss<T>(
            Dictionary<ushort, T?> cache,
            Dictionary<ushort, int> miss,
            ushort lin) where T : struct
        {
            var count = (miss.TryGetValue(lin, out var c) ? c : 0) + 1;
            miss[lin] = count;
            if (count >= _missThresholdToNull)
                cache[lin] = null;
        }

        private bool ValueEquals(DigitalIoState a, DigitalIoState b) => a.State == b.State;
        private bool ValueEquals(AnalogIoState a, AnalogIoState b) => Math.Abs(a.State - b.State) <= _analogDeadband;

        // ===== 6) Rewritten scan loops (replace existing ScanDI/DO/AI/AO methods) =====

        private async Task ScanDIAsync(bool push, CancellationToken ct)
        {
            if (_diMap.Count == 0) return;

            await WithReconnectAsync(async pac =>
            {
                int maxOcc = _diMap.Last().occ;
                for (int occ = 0; occ <= maxOcc; occ++)
                {
                    ushort word;
                    try
                    {
                        word = await pac.ReadDIWordAsync(occ, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // mark every DI in this occurrence as a miss
                        var allLin = LinearsForOccurrence(_diMap, occ);
                        lock (_stateGate) foreach (var lin in allLin) NoteMiss(_diState, _diMiss, lin);
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    lock (_stateGate)
                    {
                        foreach (var (lin, bit) in LinearsWithLeaf(_diMap, occ))
                        {
                            bool v = ((word >> (bit - 1)) & 1) == 1;
                            NoteOk(
                                _diState, _diMiss, lin,
                                new DigitalIoState(v, now),
                                push,
                                equals: static (a, b) => a.State == b.State,
                                changes: _diChanges
                            );
                        }
                    }
                }
            }, ct);
        }

        private async Task ScanDOAsync(bool push, CancellationToken ct)
        {
            if (_doMap.Count == 0) return;

            await WithReconnectAsync(async pac =>
            {
                int maxOcc = _doMap.Last().occ;
                for (int occ = 0; occ <= maxOcc; occ++)
                {
                    ushort word;
                    try
                    {
                        word = await pac.ReadDOWordAsync(occ, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        var allLin = LinearsForOccurrence(_doMap, occ);
                        lock (_stateGate) foreach (var lin in allLin) NoteMiss(_doState, _doMiss, lin);
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    lock (_stateGate)
                    {
                        foreach (var (lin, bit) in LinearsWithLeaf(_doMap, occ))
                        {
                            bool v = ((word >> (bit - 1)) & 1) == 1;
                            NoteOk(
                                _doState, _doMiss, lin,
                                new DigitalIoState(v, now),
                                push,
                                equals: static (a, b) => a.State == b.State,
                                changes: _doChanges
                            );
                        }
                    }
                }
            }, ct);
        }

        private async Task ScanAIAsync(bool push, CancellationToken ct)
        {
            if (_aiMap.Count == 0) return;

            await WithReconnectAsync(async pac =>
            {
                int maxOcc = _aiMap.Last().occ;
                for (int occ = 0; occ <= maxOcc; occ++)
                {
                    var chans = _aiMap.Where(p => p.occ == occ).Select(p => p.leaf).Distinct().OrderBy(x => x).ToArray();
                    foreach (var (start, len) in Spans(chans))
                    {
                        ushort[] words;
                        try
                        {
                            words = await pac.ReadAI2700458_RangeAsync(occ, start, len, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            // this span failed — mark only these channels as misses
                            var missLins = Enumerable.Range(start, len)
                                                     .Select(ch => LeafToLinear(_aiMap, occ, ch))
                                                     .Where(lin => lin != 0);
                            lock (_stateGate) foreach (var lin in missLins) NoteMiss(_aiState, _aiMiss, lin);
                            continue;
                        }

                        var now = DateTime.UtcNow;
                        lock (_stateGate)
                        {
                            for (int i = 0; i < len; i++)
                            {
                                int ch = start + i;
                                var lin = LeafToLinear(_aiMap, occ, ch);
                                if (lin == 0) continue;

                                var range = _aiRanges.TryGetValue(lin, out var r) ? r : _defaultAiRange;
                                ushort raw = (ushort)(words[i] & 0b_0111_1111_1111_1000);

                                double val = range == AnalogRange.mA_0_20
                                    ? Map(raw, 0, 32512, 0.0, 21.675)
                                    : Map(raw, 0, 32512, 4.0, 21.339);

                                NoteOk(
                                    _aiState, _aiMiss, lin,
                                    new AnalogIoState(val, now),
                                    push,
                                    // capture _analogDeadband from outer class:
                                    equals: (a, b) => Math.Abs(a.State - b.State) <= _analogDeadband,
                                    changes: _aiChanges
                                );
                            }
                        }
                    }
                }
            }, ct);
        }

        private async Task ScanAOAsync(bool push, CancellationToken ct)
        {
            if (_aoMap.Count == 0) return;

            await WithReconnectAsync(async pac =>
            {
                int maxOcc = _aoMap.Last().occ;
                for (int occ = 0; occ <= maxOcc; occ++)
                {
                    var chans = _aoMap.Where(p => p.occ == occ).Select(p => p.leaf).Distinct().OrderBy(x => x).ToArray();
                    foreach (var (start, len) in Spans(chans))
                    {
                        ushort[] words;
                        try
                        {
                            words = await pac.ReadAO2700775_RangeAsync(occ, start, len, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            var missLins = Enumerable.Range(start, len)
                                                     .Select(ch => LeafToLinear(_aoMap, occ, ch))
                                                     .Where(lin => lin != 0);
                            lock (_stateGate) foreach (var lin in missLins) NoteMiss(_aoState, _aoMiss, lin);
                            continue;
                        }

                        var now = DateTime.UtcNow;
                        lock (_stateGate)
                        {
                            for (int i = 0; i < len; i++)
                            {
                                int ch = start + i;
                                var lin = LeafToLinear(_aoMap, occ, ch);
                                if (lin == 0) continue;

                                var range = _aoRanges.TryGetValue(lin, out var rr) ? rr : AnalogRange.V_0_10;
                                ushort raw = (ushort)(words[i] & 0b_0111_1111_1111_1000);

                                double val = range switch
                                {
                                    AnalogRange.V_0_10 => Map(raw, 0, 32512, 0.0, 10.837),
                                    AnalogRange.mA_4_20 => Map(raw, 0, 32512, 4.0, 21.339),
                                    _ => double.NaN
                                };

                                NoteOk(
                                    _aoState, _aoMiss, lin,
                                    new AnalogIoState(val, now),
                                    push,
                                    equals: (a, b) => Math.Abs(a.State - b.State) <= _analogDeadband,
                                    changes: _aoChanges
                                );
                            }
                        }
                    }
                }
            }, ct);
        }

        // ===== 7) Tiny iterators for DI/DO helpers =====

        private IEnumerable<ushort> LinearsForOccurrence(List<(int occ, int leaf)> map, int occ)
        {
            for (ushort lin = 1; lin <= map.Count; lin++)
                if (map[lin - 1].occ == occ) yield return lin;
        }

        private IEnumerable<(ushort lin, int leaf)> LinearsWithLeaf(List<(int occ, int leaf)> map, int occ)
        {
            for (ushort lin = 1; lin <= map.Count; lin++)
                if (map[lin - 1].occ == occ) yield return (lin, map[lin - 1].leaf);
        }


        public void Dispose()
        {
            _watchdogSub?.Dispose();
            _watchdogSub = null;

            try { _master?.Dispose(); } catch { }
            _master = null;
            _pac = null;

            if (!_isConnected.IsDisposed) _isConnected.OnNext(false);
            _isConnected.Dispose();
        }

        // ------------------------- Reconnection core -------------------------


        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (_master != null && await IsMasterConnectedAsync(_master, ct).ConfigureAwait(false))
            {
                PublishConnection(true);
                return;
            }

            await _reconnectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_master != null && await IsMasterConnectedAsync(_master, ct).ConfigureAwait(false))
                {
                    PublishConnection(true);
                    return;
                }

                try { _master?.Dispose(); } catch { }
                _master = null; _pac = null;
                PublishConnection(false);

                // backoff + jitter
                if (_backoff == TimeSpan.Zero) _backoff = BackoffMin;
                var jitterMs = _rng.Next(0, 200);
                await Task.Delay(_backoff + TimeSpan.FromMilliseconds(jitterMs), ct).ConfigureAwait(false);

                _master = await _masterFactory(ct).ConfigureAwait(false);
                _pac = new Inline2701388(_master);

                if (!await IsMasterConnectedAsync(_master, ct).ConfigureAwait(false))
                    throw new SocketException((int)SocketError.TimedOut);

                await _pac.InitializeAsync(ct).ConfigureAwait(false);
                await BuildMapsAsync(ct).ConfigureAwait(false);

                PublishConnection(true);
                _backoff = BackoffMin;
            }
            catch
            {
                var nextMs = Math.Min(BackoffMax.TotalMilliseconds,
                                      Math.Max(BackoffMin.TotalMilliseconds,
                                               (_backoff == TimeSpan.Zero ? BackoffMin : TimeSpan.FromMilliseconds(_backoff.TotalMilliseconds * 2)).TotalMilliseconds));
                _backoff = TimeSpan.FromMilliseconds(nextMs);
                PublishConnection(false);
                throw;
            }
            finally
            {
                _reconnectLock.Release();
            }
        }

        private static async Task<bool> IsMasterConnectedAsync(IModbusMaster master, CancellationToken ct)
        {
            try
            {
                // your connectivity check
                await master.ReadInputRegistersAsync(0, 1400, 1).ConfigureAwait(false);
                return true;
            }
            catch { return false; }
        }

        private async Task<T> WithReconnectAsync<T>(Func<Inline2701388, Task<T>> action, CancellationToken ct)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var pac = _pac!;

            try { return await action(pac).ConfigureAwait(false); }
            catch
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                pac = _pac!;
                return await action(pac).ConfigureAwait(false);
            }
        }

        private async Task WithReconnectAsync(Func<Inline2701388, Task> action, CancellationToken ct)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            var pac = _pac!;

            try { await action(pac).ConfigureAwait(false); }
            catch
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                pac = _pac!;
                await action(pac).ConfigureAwait(false);
            }
        }

        // ------------------------- Map building & cache sizing -------------------------

        private async Task BuildMapsAsync(CancellationToken ct)
        {
            _diMap.Clear(); _doMap.Clear(); _aiMap.Clear(); _aoMap.Clear();
            var pac = _pac ?? throw new InvalidOperationException("PAC not connected.");

            // DI
            for (int occ = 0; ; occ++)
            {
                try { _ = await pac.ReadDIWordAsync(occ, ct).ConfigureAwait(false); }
                catch (KeyNotFoundException) { break; }
                for (int b = 1; b <= 16; b++) _diMap.Add((occ, b));
            }

            // DO
            for (int occ = 0; ; occ++)
            {
                try { _ = await pac.ReadDOWordAsync(occ, ct).ConfigureAwait(false); }
                catch (KeyNotFoundException) { break; }
                for (int b = 1; b <= 16; b++) _doMap.Add((occ, b));
            }

            // AI (2700458)
            try { await pac.ConfigureAI2700458Async(_defaultAiRange, ct).ConfigureAwait(false); } catch { /* optional */ }
            for (int occ = 0; ; occ++)
            {
                try { _ = await pac.ReadAI2700458_RangeAsync(occ, 1, 1, ct).ConfigureAwait(false); }
                catch (KeyNotFoundException) { break; }
                for (int ch = 1; ch <= 4; ch++) _aiMap.Add((occ, ch));
            }

            // AO (2700775)
            for (int occ = 0; ; occ++)
            {
                try
                {
                    await pac.ConfigureAO2700775Async(_defaultAoRanges.ch1, _defaultAoRanges.ch2, ct).ConfigureAwait(false);
                    _ = await pac.ReadAO2700775_RangeAsync(occ, 1, 1, ct).ConfigureAwait(false);
                }
                catch (KeyNotFoundException) { break; }
                for (int ch = 1; ch <= 2; ch++) _aoMap.Add((occ, ch));
            }

            // Resize ranges & states
            lock (_stateGate)
            {
                RefillRangeMap(_aiRanges, (ushort)_aiMap.Count, _ => _defaultAiRange);
                RefillRangeMap(_aoRanges, (ushort)_aoMap.Count, i => ((i - 1) % 2 == 0) ? _defaultAoRanges.ch1 : _defaultAoRanges.ch2);

                RefillStateMap(_diState, (ushort)_diMap.Count);
                RefillStateMap(_doState, (ushort)_doMap.Count);
                RefillStateMap(_aiState, (ushort)_aiMap.Count);
                RefillStateMap(_aoState, (ushort)_aoMap.Count);
            }

            static void RefillRangeMap<T>(Dictionary<ushort, T> map, ushort count, Func<ushort, T> def)
            {
                for (ushort i = 1; i <= count; i++) if (!map.ContainsKey(i)) map[i] = def(i);
                foreach (var k in map.Keys.Where(k => k > count).ToList()) map.Remove(k);
            }
            static void RefillStateMap<T>(Dictionary<ushort, T?> map, ushort count) where T : struct
            {
                for (ushort i = 1; i <= count; i++) if (!map.ContainsKey(i)) map[i] = null;
                foreach (var k in map.Keys.Where(k => k > count).ToList()) map.Remove(k);
            }
        }

        // ------------------------- IIOBus implementation -------------------------

        public Task WriteDigitalOutputsAsync(IReadOnlyDictionary<ushort, bool> states, CancellationToken ct = default)
            => WithReconnectAsync(async pac =>
            {
                if (states == null || states.Count == 0) return;
                var req = Normalize(states.Keys, (ushort)_doMap.Count, "DO");
                var byOcc = GroupByOccurrence(req, _doMap);

                foreach (var (occ, linList) in byOcc)
                {
                    ushort current = await pac.ReadDOWordAsync(occ, ct).ConfigureAwait(false);
                    ushort updated = current;

                    foreach (var lin in linList)
                    {
                        int bit = _doMap[lin - 1].leaf;
                        bool state = states[lin];
                        updated = state ? (ushort)(updated | (1 << (bit - 1)))
                                        : (ushort)(updated & ~(1 << (bit - 1)));
                    }

                    if (updated != current)
                        await pac.WriteDOWordAsync(occ, updated, ct).ConfigureAwait(false);

                    var now = DateTime.UtcNow;
                    lock (_stateGate)
                        foreach (var lin in linList)
                            _doState[lin] = new DigitalIoState(states[lin], now);
                }
            }, ct);

        public Task WriteDigitalOutputAsync(ushort index, bool state, CancellationToken ct = default)
            => WriteDigitalOutputsAsync(new Dictionary<ushort, bool> { [index] = state }, ct);

        public Task<Dictionary<ushort, bool>> ReadDigitalOutputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
            => WithReconnectAsync(async pac =>
            {
                var req = Normalize(indexes, (ushort)_doMap.Count, "DO");
                var byOcc = GroupByOccurrence(req, _doMap);

                var result = new Dictionary<ushort, bool>(req.Count);
                foreach (var (occ, linList) in byOcc)
                {
                    var word = await pac.ReadDOWordAsync(occ, ct).ConfigureAwait(false);
                    var now = DateTime.UtcNow;
                    lock (_stateGate)
                    {
                        foreach (var lin in linList)
                        {
                            int bit = _doMap[lin - 1].leaf;
                            bool v = ((word >> (bit - 1)) & 1) == 1;
                            result[lin] = v;
                            _doState[lin] = new DigitalIoState(v, now);
                        }
                    }
                }
                return result;
            }, ct);

        public Task<bool> ReadDigitalOutputAsync(ushort index, CancellationToken ct = default)
            => ReadDigitalOutputsAsync(new[] { index }, ct).ContinueWith(t => t.Result[index], ct);

        public Task<Dictionary<ushort, bool>> ReadDigitalInputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
            => WithReconnectAsync(async pac =>
            {
                var req = Normalize(indexes, (ushort)_diMap.Count, "DI");
                var byOcc = GroupByOccurrence(req, _diMap);

                var result = new Dictionary<ushort, bool>(req.Count);
                foreach (var (occ, linList) in byOcc)
                {
                    var word = await pac.ReadDIWordAsync(occ, ct).ConfigureAwait(false);
                    var now = DateTime.UtcNow;
                    lock (_stateGate)
                    {
                        foreach (var lin in linList)
                        {
                            int bit = _diMap[lin - 1].leaf;
                            bool v = ((word >> (bit - 1)) & 1) == 1;
                            result[lin] = v;
                            _diState[lin] = new DigitalIoState(v, now);
                        }
                    }
                }
                return result;
            }, ct);

        public Task<bool> ReadDigitalInputAsync(ushort index, CancellationToken ct = default)
            => ReadDigitalInputsAsync(new[] { index }, ct).ContinueWith(t => t.Result[index], ct);

        public Task WriteAnalogOutputAsync(ushort index, double value, CancellationToken ct = default)
            => WithReconnectAsync(async pac =>
            {
                RequireIndex(index, (ushort)_aoMap.Count, "AO");
                var (occ, ch) = _aoMap[index - 1];

                var (r1, r2) = GetAoOccurrenceRanges(occ);
                await pac.ConfigureAO2700775Async(r1, r2, ct).ConfigureAwait(false);

                var range = _aoRanges.TryGetValue(index, out var rr) ? rr : AnalogRange.V_0_10;
                await pac.WriteAO2700775Async(ch, value, range, ct).ConfigureAwait(false);

                lock (_stateGate) _aoState[index] = new AnalogIoState(value, DateTime.UtcNow);
            }, ct);

        public Task WriteAnalogOutputsAsync(IReadOnlyDictionary<ushort, double> values, CancellationToken ct = default)
            => WithReconnectAsync(async pac =>
            {
                if (values == null || values.Count == 0) return;
                var req = Normalize(values.Keys, (ushort)_aoMap.Count, "AO");
                var byOcc = GroupByOccurrence(req, _aoMap);

                foreach (var (occ, linList) in byOcc)
                {
                    var (r1, r2) = GetAoOccurrenceRanges(occ);
                    await pac.ConfigureAO2700775Async(r1, r2, ct).ConfigureAwait(false);

                    foreach (var lin in linList)
                    {
                        var (o, ch) = _aoMap[lin - 1];
                        var range = _aoRanges.TryGetValue(lin, out var rr) ? rr : AnalogRange.V_0_10;
                        var val = values[lin];
                        await pac.WriteAO2700775Async(ch, val, range, ct).ConfigureAwait(false);

                        lock (_stateGate) _aoState[lin] = new AnalogIoState(val, DateTime.UtcNow);
                    }
                }
            }, ct);

        public Task<IReadOnlyDictionary<ushort, double>> ReadAnalogOutputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
            => WithReconnectAsync(async pac =>
            {
                var req = Normalize(indexes, (ushort)_aoMap.Count, "AO");
                var byOcc = GroupByOccurrence(req, _aoMap);

                var result = new Dictionary<ushort, double>(req.Count);
                foreach (var (occ, linList) in byOcc)
                {
                    var chans = linList.Select(lin => _aoMap[lin - 1].leaf).Distinct().OrderBy(x => x).ToArray();
                    foreach (var (start, len) in Spans(chans))
                    {
                        var words = await pac.ReadAO2700775_RangeAsync(occ, start, len, ct).ConfigureAwait(false);
                        for (int i = 0; i < len; i++)
                        {
                            int ch = start + i;
                            ushort raw = (ushort)(words[i] & 0b_0111_1111_1111_1000);
                            ushort lin = LeafToLinear(_aoMap, occ, ch);
                            var range = _aoRanges.TryGetValue(lin, out var rr) ? rr : AnalogRange.V_0_10;

                            double val = range switch
                            {
                                AnalogRange.V_0_10 => Map(raw, 0, 32512, 0.0, 10.837),
                                AnalogRange.mA_4_20 => Map(raw, 0, 32512, 4.0, 21.339),
                                _ => throw new NotSupportedException("AO supports 0–10 V or 4–20 mA.")
                            };
                            result[lin] = val;
                            lock (_stateGate) _aoState[lin] = new AnalogIoState(val, DateTime.UtcNow);
                        }
                    }
                }
                return (IReadOnlyDictionary<ushort, double>)result;
            }, ct);

        public Task<double> ReadAnalogInputAsync(ushort index, CancellationToken ct = default)
            => ReadAnalogInputsAsync(new[] { index }, ct).ContinueWith(t => t.Result[index], ct);

        public Task<IReadOnlyDictionary<ushort, double>> ReadAnalogInputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
            => WithReconnectAsync(async pac =>
            {
                var req = Normalize(indexes, (ushort)_aiMap.Count, "AI");
                var byOcc = GroupByOccurrence(req, _aiMap);

                var result = new Dictionary<ushort, double>(req.Count);
                foreach (var (occ, linList) in byOcc)
                {
                    var chans = linList.Select(lin => _aiMap[lin - 1].leaf).Distinct().OrderBy(x => x).ToArray();
                    foreach (var (start, len) in Spans(chans))
                    {
                        var words = await pac.ReadAI2700458_RangeAsync(occ, start, len, ct).ConfigureAwait(false);
                        for (int i = 0; i < len; i++)
                        {
                            int ch = start + i;
                            var lin = LeafToLinear(_aiMap, occ, ch);
                            var range = _aiRanges.TryGetValue(lin, out var r) ? r : _defaultAiRange;

                            ushort raw = (ushort)(words[i] & 0b_0111_1111_1111_1000);
                            double val = range == AnalogRange.mA_0_20
                                ? Map(raw, 0, 32512, 0.0, 21.675)
                                : Map(raw, 0, 32512, 4.0, 21.339);

                            result[lin] = val;
                            lock (_stateGate) _aiState[lin] = new AnalogIoState(val, DateTime.UtcNow);
                        }
                    }
                }
                return (IReadOnlyDictionary<ushort, double>)result;
            }, ct);

        // ------------------------- IPhoenixProxy additions -------------------------

        public async Task SetDigitalOutputStateAsync(ushort index, bool state, CancellationToken ct)
            => await WriteDigitalOutputAsync(index, state, ct).ConfigureAwait(false);

        public async Task SetAnalogOutputStateAsync(ushort index, double state, CancellationToken ct)
            => await WriteAnalogOutputAsync(index, state, ct).ConfigureAwait(false);

        public IReadOnlyDictionary<ushort, DigitalIoState?> DigitalInputState
        {
            get { lock (_stateGate) return new ReadOnlyDictionary<ushort, DigitalIoState?>(new Dictionary<ushort, DigitalIoState?>(_diState)); }
        }
        public IReadOnlyDictionary<ushort, DigitalIoState?> DigitalOutputState
        {
            get { lock (_stateGate) return new ReadOnlyDictionary<ushort, DigitalIoState?>(new Dictionary<ushort, DigitalIoState?>(_doState)); }
        }
        public IReadOnlyDictionary<ushort, AnalogIoState?> AnalogInputState
        {
            get { lock (_stateGate) return new ReadOnlyDictionary<ushort, AnalogIoState?>(new Dictionary<ushort, AnalogIoState?>(_aiState)); }
        }
        public IReadOnlyDictionary<ushort, AnalogIoState?> AnalogOutputState
        {
            get { lock (_stateGate) return new ReadOnlyDictionary<ushort, AnalogIoState?>(new Dictionary<ushort, AnalogIoState?>(_aoState)); }
        }

        // ------------------------- Optional range APIs -------------------------

        public void SetAiRange(ushort linearIndex, AnalogRange range)
        {
            RequireIndex(linearIndex, (ushort)_aiMap.Count, "AI");
            if (range is not AnalogRange.mA_0_20 and not AnalogRange.mA_4_20)
                throw new NotSupportedException("AI 2700458 supports 0–20 mA or 4–20 mA.");
            _aiRanges[linearIndex] = range;
        }

        public Task SetAoRangeAsync(ushort linearIndex, AnalogRange range, CancellationToken ct = default)
            => WithReconnectAsync(async pac =>
            {
                RequireIndex(linearIndex, (ushort)_aoMap.Count, "AO");
                if (range is not AnalogRange.V_0_10 and not AnalogRange.mA_4_20)
                    throw new NotSupportedException("AO 2700775 supports 0–10 V or 4–20 mA.");
                _aoRanges[linearIndex] = range;

                var (occ, _) = _aoMap[linearIndex - 1];
                var (r1, r2) = GetAoOccurrenceRanges(occ);
                await pac.ConfigureAO2700775Async(r1, r2, ct).ConfigureAwait(false);
            }, ct);

        // ------------------------- Helpers -------------------------

        private (AnalogRange ch1, AnalogRange ch2) GetAoOccurrenceRanges(int occ)
        {
            ushort baseLin = (ushort)(occ * 2 + 1);
            var ch1 = _aoRanges.TryGetValue(baseLin, out var r1) ? r1 : _defaultAoRanges.ch1;
            var ch2 = _aoRanges.TryGetValue((ushort)(baseLin + 1), out var r2) ? r2 : _defaultAoRanges.ch2;
            return (ch1, ch2);
        }

        private static List<ushort> Normalize(IEnumerable<ushort> idxs, ushort count, string label)
        {
            if (idxs == null) throw new ArgumentNullException(nameof(idxs));
            var list = idxs.Distinct().ToList();
            foreach (var i in list) RequireIndex(i, count, label);
            return list;
        }

        private static void RequireIndex(ushort idx1, ushort count, string label)
        {
            if (idx1 < 1 || idx1 > count)
                throw new ArgumentOutOfRangeException($"{label} index {idx1} is out of range 1..{count}");
        }

        private static Dictionary<int, List<ushort>> GroupByOccurrence(IEnumerable<ushort> linears, List<(int occ, int leaf)> map)
        {
            var byOcc = new Dictionary<int, List<ushort>>();
            foreach (var lin in linears)
            {
                var occ = map[lin - 1].occ;
                if (!byOcc.TryGetValue(occ, out var list)) byOcc[occ] = list = new List<ushort>();
                list.Add(lin);
            }
            return byOcc;
        }

        private static IEnumerable<(int start, int len)> Spans(int[] sortedDistinct)
        {
            if (sortedDistinct.Length == 0) yield break;
            int start = sortedDistinct[0], prev = start;
            for (int i = 1; i < sortedDistinct.Length; i++)
            {
                if (sortedDistinct[i] == prev + 1) { prev = sortedDistinct[i]; continue; }
                yield return (start, prev - start + 1);
                start = prev = sortedDistinct[i];
            }
            yield return (start, prev - start + 1);
        }

        private static ushort LeafToLinear(List<(int occ, int leaf)> map, int occ, int leaf)
        {
            for (ushort i = 0; i < map.Count; i++)
                if (map[i].occ == occ && map[i].leaf == leaf) return (ushort)(i + 1);
            throw new KeyNotFoundException("Leaf not found in map.");
        }

        private static double Map(double x, double inMin, double inMax, double outMin, double outMax)
            => (x - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;
    }
}



