using NModbus;
using NModbus.Extensions.Enron;
using NModbus.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace puck.Phoenix
{

    public static class RangeMapper
    {
        public static double MapValueToRange(
            double value,
            double sourceRangeMin,
            double sourceRangeMax,
            double targetRangeMin,
            double targetRangeMax)
        {
            //map using y = mx + B where:
            //m = (targetRangeMax - targetRangeMin)/(sourceRangeMax - sourceRangeMin)
            //B = targetRangeMin
            //x = value - sourceRangeMin 
            //y = output
            var y = (value - sourceRangeMin) * ((targetRangeMax - targetRangeMin) / (sourceRangeMax - sourceRangeMin)) + targetRangeMin;

            return y;
        }
    }

    public abstract class PhoenixIOModuleBase
    {
        protected readonly IModbusMaster _modbusServ;
        protected readonly ushort _startAddress;
        protected readonly ushort _endAddress;
        protected readonly ushort _wordCount;

        public PhoenixIOModuleBase(
            IModbusMaster modbusMaster,
            ushort startAddress,
            ushort wordCount)
        {
            _wordCount = wordCount;
            _endAddress = (ushort)(startAddress + wordCount - 1);
            _startAddress = startAddress;
            _modbusServ = modbusMaster;
        }
    }

    public interface IIOModule
    {
        ushort GetNumberOfIOPoints();
    }

    public abstract class PhoenixDigitalModuleBase : PhoenixIOModuleBase, IIOModule
    {
        protected readonly ushort _bitCount;

        public PhoenixDigitalModuleBase(
            IModbusMaster modbusMaster,
            ushort startAddress,
            ushort bitCount,
            ushort wordCount) : base(modbusMaster, startAddress, wordCount)
        {
            _bitCount = bitCount;
        }


        protected ushort GetUpstreamBits(
            ushort endAddress,
            ushort targetAddess)
        {
            //calculates the start address for a given register within a module
            ushort ioIndex = (ushort)(((endAddress - targetAddess) * 16));
            return ioIndex;
        }

        protected ushort GetTargetAddressByIOIndex(ushort ioIndex, ushort endAddress)
        {
            //register addresses decrease as io index increases
            //This is reversed from the expected mapping of io indexes onto register addresses
            //given 1-based io index 16 and end address 5, target address is 5
            ushort targetAddress = (ushort)(endAddress - ((ioIndex - 1) / 16));
            return targetAddress;
        }

        protected ushort GetRegisterIndex(ushort ioIndex, ushort targetAddress, ushort endAddress)
        {
            //adjust index according to target register frame of reference(i.e. get index within the target register from io index)
            return (ushort)(ioIndex - (endAddress - targetAddress) * 16);
        }


        public ushort GetNumberOfIOPoints() => _bitCount;

    }

    public interface IDigitalInputModule : IIOModule
    {
        Task<bool> ReadAsync(ushort ioIndex, CancellationToken ct = default);
        Task<IReadOnlyDictionary<ushort, bool>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default);
    }

    public class PhoenixDigitalInputModule : PhoenixDigitalModuleBase, IDigitalInputModule
    {
        public PhoenixDigitalInputModule(
            IModbusMaster modbusMaster,
            ushort startAddress,
            ushort bitCount,
            ushort wordCount)
            : base(modbusMaster, startAddress, bitCount, wordCount)
        {
        }

        protected void ValidateIndex(ushort index)
        {
            string failPrefix = "Failed to validate input index. ";
            if (index < 1 || index > _bitCount)
                throw new ArgumentOutOfRangeException($"{failPrefix} Specified index was outside of allowed range of 1-{_bitCount}");
        }


        #region IDigitalInputModule

        public async Task<IReadOnlyDictionary<ushort, bool>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            var indexHash = new HashSet<ushort>(indexes);

            //READ ALL REGISTERS FOR THIS MODULE
            var regVals =
                (await _modbusServ.ReadInputRegistersAsync(0, _startAddress, _wordCount)
                .ConfigureAwait(false));

            var stateMap = new Dictionary<ushort, bool>();

            for (int i = 0; i < regVals.Length; i++)
            {
                var currentAddress = _startAddress + i;
                int upstreamBitCount = (_endAddress - currentAddress) * 16;

                for (int j = 0; j < 16; j++)
                {
                    //CALCULATE REQUESTED 1-BASED INDEX REPRESENTATION
                    ushort index = (ushort)(upstreamBitCount + j + 1);

                    //only handle ones we care about
                    if (indexHash.Contains(index))
                    {
                        //shift to correct bit, isolate, and see if high or low
                        //phoenix input 0 maps to LSB i.e. bit 0 
                        bool state = ((regVals[i] >> j) & 1) == 1;
                        stateMap.Add(index, state);
                    }
                }
            }

            return stateMap;
        }

        public async Task<bool> ReadAsync(ushort ioIndex, CancellationToken ct = default)
        {
            if (ioIndex < 1 || ioIndex > _bitCount)
                throw new ArgumentOutOfRangeException($"Failed to validate input index. Specified index was outside of allowed range of 1-{_bitCount}");

            //register addresses decrease as io index increases
            //This is reversed from the expected mapping of io indexes onto register addresses
            //given 1-based io index 16 and end address 5, target address is 5
            ushort targetAddress = (ushort)(_endAddress - ((ioIndex - 1) / 16));

            //adjust target register address according to requested io index
            ushort targetWordAddress = GetTargetAddressByIOIndex(ioIndex, _endAddress);

            var val =
                (await _modbusServ.ReadInputRegistersAsync(0, targetWordAddress, 1).ConfigureAwait(false))[0];

            //adjust index according to target register frame of reference
            //(i.e. get index within the target register from io index)
            ushort adjustedIndex = (ushort)(ioIndex - (_endAddress - targetAddress) * 16);

            var state = ((val >> (16 - adjustedIndex)) & 1) == 1;
            return state;
        }

        #endregion
    }

    public interface IDigitalOutputModule : IDigitalInputModule, IIOModule
    {
        Task WriteAsync(ushort index, bool value, CancellationToken ct = default);
        Task WriteManyAsync(IReadOnlyDictionary<ushort, bool> outputStates, CancellationToken ct = default);
    }

    public class PhoenixDigitalOutputModule : PhoenixDigitalInputModule, IDigitalOutputModule
    {

        public PhoenixDigitalOutputModule(
            IModbusMaster modbusServ,
            ushort startAddress,
            ushort bitCount,
            ushort wordCount)
            : base(modbusServ, startAddress, bitCount, wordCount)
        {
        }

        protected void ValidateOutputStates(IReadOnlyDictionary<ushort, bool> outputStates)
        {
            string failPrefix = "Failed while validating output states. ";
            if (outputStates.Any(o => o.Key > _bitCount))
                throw new ArgumentOutOfRangeException($"{failPrefix} Specified IO index cannot be greater than number bits representing the IO: {_bitCount}");
        }

        public async Task WriteManyAsync(IReadOnlyDictionary<ushort, bool> outputStates, CancellationToken ct = default)
        {
            //group outputs by register address
            var registerGroupingDictionary =
                outputStates
                .Select(s =>
                {
                    var registerAddress = GetTargetAddressByIOIndex(s.Key, _endAddress);
                    var output = new
                    {
                        register = registerAddress,
                        indexInReg = GetRegisterIndex(s.Key, registerAddress, _endAddress),
                        state = s.Value
                    };

                    return output;
                })
                .GroupBy(s => s.register)
                .ToDictionary(s => s.Key, s => s.Select(g => g));

            var addresses = new HashSet<ushort>(registerGroupingDictionary.Select(a => a.Key));
            var maxAddress = addresses.Max();
            var minAddress = addresses.Min();

            var registers =
                await _modbusServ.ReadInputRegistersAsync(0, minAddress, (ushort)(maxAddress - minAddress + 1));

            for (int i = 0; i < registers.Length; i++)
            {
                var regAddr = (ushort)(minAddress + i);

                if (addresses.Contains(regAddr))
                {
                    ushort updatedRegVal = registers[i];

                    //get the index of all high states for activation bitmask
                    var highStates =
                        registerGroupingDictionary[regAddr]
                        .Where(j => j.state == true)
                        .Select(j => j.indexInReg)
                        .ToList();

                    if (highStates.Count > 0)
                        updatedRegVal = (ushort)(updatedRegVal | ConstructSelectivelyHighBitMaskMsbFirst(highStates));

                    //get the index of all low states for deactivation bitmask
                    var lowStates =
                        registerGroupingDictionary[regAddr]
                        .Where(j => j.state == false)
                        .Select(j => j.indexInReg)
                        .ToList();

                    if (lowStates.Count > 0)
                        updatedRegVal = (ushort)(updatedRegVal & ConstructSelectivelyLowBitMaskMsbFirst(lowStates));

                    await _modbusServ.WriteSingleRegisterAsync(0, minAddress, updatedRegVal);
                }
            }
        }

        public ushort ConstructSelectivelyHighBitMaskMsbFirst(IReadOnlyList<ushort> targetHighBitIndexes)
        {
            int start = 0b00000000_00000000;

            foreach (var targetIndex in targetHighBitIndexes)
                start |= 1 << (targetIndex - 1);

            return Convert.ToUInt16(start);
        }

        public ushort ConstructSelectivelyLowBitMaskMsbFirst(IReadOnlyList<ushort> targetHighBitIndexes)
        {
            int start = 0b11111111_11111111;

            foreach (var targetIndex in targetHighBitIndexes)
                start ^= 1 << (targetIndex - 1);

            return Convert.ToUInt16(start);
        }

        public async Task WriteAsync(ushort index, bool value, CancellationToken ct = default)
        {
            ushort targetAddress = GetTargetAddressByIOIndex(index, _endAddress);
            ushort indexInRegister = GetRegisterIndex(index, targetAddress, _endAddress);

            var currentRegister = (await _modbusServ.ReadInputRegistersAsync(0, targetAddress, 1))[0];
            var targetIOPoints = new List<ushort>() { indexInRegister };

            ushort updatedRegister =
                value ?
                (ushort)(currentRegister | ConstructSelectivelyHighBitMaskMsbFirst(targetIOPoints))
                :
                (ushort)(currentRegister & ConstructSelectivelyLowBitMaskMsbFirst(targetIOPoints));

            await _modbusServ.WriteSingleRegisterAsync(0, targetAddress, updatedRegister);
        }
    }

    public interface IAnalogInputModule : IIOModule
    {
        Task InitializeAsync(TimeSpan initTimeout, CancellationToken ct = default);
        Task<double> ReadAsync(ushort ioIndex, CancellationToken ct = default);
        Task<IReadOnlyDictionary<ushort, double>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default);
        void SetInputConfiguration(IReadOnlyDictionary<ushort, AnalogInputConfig> configMap);
    }


    public enum SampleAverageAmount
    {
        Four,
        Sixteen,
        ThirtyTwo
    }

    public enum AnalogMeasurementRange
    {
        ZeroToTwentyMilliamps,
        FourToTwentyMilliamps,
        PlusMinusTwentyMilliamps,
        ZeroToTenVolts,
        ZeroToFiveVolts,
        PlusMinusTenVolts,
        PlusMinusFiveVolts,
        ZeroToFortyMilliamps,
        PlusMinusFortyMilliamps,
    }

    public struct AnalogInputScaling
    {
        public AnalogInputScaling(double minInputValue, double maxInputValue, short minRegisterValue, short maxRegisterValue)
        {
            MinInputValue = minInputValue;
            MaxInputValue = maxInputValue;
            MinRegisterValue = minRegisterValue;
            MaxRegisterValue = maxRegisterValue;
        }
        public short MinRegisterValue { get; }
        public short MaxRegisterValue { get; }
        public double MinInputValue { get; }
        public double MaxInputValue { get; }
    }

    /// <summary>
    /// base functionality for 2700458, 2878447, 2700458
    /// </summary>
    public abstract class PhoenixAnalogInputModuleBase : IAnalogInputModule
    {
        protected readonly IModbusMaster _modbusServ;

        protected readonly ushort _dataOutWordCount;
        protected readonly ushort _dataInStartAddress;
        protected ushort _dataOutStartAddress;

        protected IReadOnlyDictionary<ushort, AnalogInputConfig> _configMap;

        public PhoenixAnalogInputModuleBase(
            IModbusMaster modbusServ,
            ushort dataInStartAddress,
            ushort dataOutStartAddress,
            ushort dataOutWordCount)
        {
            _modbusServ = modbusServ;

            _dataOutWordCount = dataOutWordCount;
            _dataInStartAddress = dataInStartAddress;
            _dataOutStartAddress = dataOutStartAddress;
        }

        // a word is one-to-one with a analog value, at least for 2700458, 2878447
        public abstract ushort GetNumberOfIOPoints();

        public async Task<double> ReadAsync(ushort ioIndex, CancellationToken ct = default) => (await ReadManyAsync(new List<ushort>() { ioIndex }, ct)).First().Value;
        public abstract Task InitializeAsync(TimeSpan initTimeout, CancellationToken ct = default);
        public abstract Task<IReadOnlyDictionary<ushort, double>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default);

        /// <summary>
        /// Dependent on Config Format (ie: IB IL or IB ST, and on module)
        /// This function is for 15 bit: 1st is sign bit, 15 data bits
        /// </summary>
        /// <param name="registerValue"></param>
        /// <returns></returns>
        virtual protected double DecodeRegisterValue(ushort registerValue)
        {
            bool isNegative = registerValue >> 15 == 1;
            var data = (registerValue & 0b_01111111_11111111);
            data = isNegative ? -data : data;

            return data;
        }

        protected double ConvertAnalogInputDataToValue(double analogInputValue, AnalogInputScaling scaling)
        {
            var output = RangeMapper.MapValueToRange(analogInputValue, scaling.MinRegisterValue, scaling.MaxRegisterValue, scaling.MinInputValue, scaling.MaxInputValue);

            return output;
        }

        public void SetInputConfiguration(IReadOnlyDictionary<ushort, AnalogInputConfig> configMap)
        {
            _configMap = configMap;
        }
    }

    /// <summary>
    /// base functionality for 2700458, 2878447
    /// </summary>
    public abstract class PhoenixAnalogInputModule : PhoenixAnalogInputModuleBase
    {
        public PhoenixAnalogInputModule(
            IModbusMaster modbusServ,
            ushort dataInStartAddress,
            ushort dataOutStartAddress,
            ushort dataOutWordCount)
            : base(modbusServ, dataInStartAddress, dataOutStartAddress, dataOutWordCount)
        {

        }

        protected async Task WriteCommandUntilMirrored(
             ushort value,
             TimeSpan timeout,
             CancellationToken ct = default)
        {
            ushort readVal = ushort.MaxValue;
            var sw = new Stopwatch();
            sw.Start();

            while (readVal != value)
            {
                await _modbusServ.WriteSingleRegisterAsync(0, _dataOutStartAddress, value);
                readVal = (await _modbusServ.ReadInputRegistersAsync(0, _dataInStartAddress, 1))[0];
                //mask off bits of interest for while comparison
                readVal &= 0b01111111_00000000;

                if (sw.Elapsed > timeout)
                {
                    readVal = (await _modbusServ.ReadInputRegistersAsync(0, _dataInStartAddress, 1))[0];

                    if (readVal >> 15 == 1)
                    {
                        var errorVal = (await _modbusServ.ReadInputRegistersAsync(0, (ushort)(_dataInStartAddress + 1), 1))[0];

                        throw new Exception(
                            $"Timed out while waiting for mirrored command to match written command. " +
                            $"Status register indicated error. Status word IN0: {readVal}" +
                            $"Error word: {errorVal}");
                    }

                    throw new Exception("Timed out while waiting for mirrored command to match written command");
                }
            }
        }
    }

    /// <summary>
    /// P/N 2700458; id code: 127   4 channel 
    /// </summary>
    public class PhoenixAnalogInputModule_2700458 : PhoenixAnalogInputModuleBase
    {
        protected readonly ushort _dataInWordCount;
        private readonly SampleAverageAmount _defaultSampleAvgConfig;
        private readonly AnalogMeasurementRange _defaultMeasurementConfig;

        private readonly IReadOnlyDictionary<AnalogMeasurementRange, AnalogInputScaling> _analogInputScalingValues = new Dictionary<AnalogMeasurementRange, AnalogInputScaling>()
        {
            {AnalogMeasurementRange.ZeroToTwentyMilliamps, new AnalogInputScaling(0, 21.675, 0, 32512)},
            {AnalogMeasurementRange.FourToTwentyMilliamps, new AnalogInputScaling(4, 21.339, 0, 32512)}
        };

        public PhoenixAnalogInputModule_2700458(
            IModbusMaster modbusServ,
            ushort dataInStartAddress,
            ushort dataOutStartAddress,
            ushort dataInWordCount,
            ushort dataOutWordCount,
            SampleAverageAmount defaultSampleAvgConfig = SampleAverageAmount.Four,
            AnalogMeasurementRange defaultMeasurementConfig = AnalogMeasurementRange.FourToTwentyMilliamps)
            : base(modbusServ, dataInStartAddress, dataOutStartAddress, dataOutWordCount)
        {
            _defaultMeasurementConfig = defaultMeasurementConfig;
            _defaultSampleAvgConfig = defaultSampleAvgConfig;

            _dataInWordCount = dataInWordCount;
        }

        protected ushort BuildConfigurationWord(
            SampleAverageAmount sampleAvgAmt,
            AnalogMeasurementRange measurementRange)
        {
            int val = 0;
            //apply config flag so actually gets written
            val |= 0b_10000000_00000000;

            switch (sampleAvgAmt)
            {
                case SampleAverageAmount.Four:
                    val |= 0b_00000010_00000000;
                    break;
                case SampleAverageAmount.Sixteen:
                    break;
                case SampleAverageAmount.ThirtyTwo:
                    val |= 0b_00000011_00000000;
                    break;
                default:
                    throw new Exception("Failed to build module configuration word. Sample average count not recognized");
            }

            switch (measurementRange)
            {
                case AnalogMeasurementRange.ZeroToTwentyMilliamps:
                    val |= 0b_00000000_00000100;
                    break;
                case AnalogMeasurementRange.FourToTwentyMilliamps:
                    val |= 0b_00000000_00000110;
                    break;
                default:
                    throw new Exception("Failed to build module configuration. Measurement range not recognized");
            }

            return (ushort)val;
        }

        // a word is one-to-one with a analog value
        public override ushort GetNumberOfIOPoints() => _dataInWordCount;

        public override async Task InitializeAsync(TimeSpan initTimeout, CancellationToken ct = default)
        {

            for (ushort i = _dataOutStartAddress; i < _dataOutStartAddress + _dataOutWordCount; i++)
            {
                ushort ioIndex = (ushort)(i - _dataInStartAddress + 1);

                var measurementRange = _defaultMeasurementConfig;
                var sampleAvg = _defaultSampleAvgConfig;

                if (_configMap.TryGetValue(ioIndex, out var config))
                {
                    measurementRange = config.MeasurementType;
                    sampleAvg = config.SampleAverageAmount;
                }

                var configWord = BuildConfigurationWord(sampleAvg, measurementRange);

                await _modbusServ.WriteSingleRegisterAsync(0, i, configWord);
            }
        }

        public override async Task<IReadOnlyDictionary<ushort, double>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            var output = new Dictionary<ushort, double>();

            foreach (var index in indexes)
            {
                var val =
                    (await _modbusServ.ReadInputRegistersAsync(0, (ushort)(_dataInStartAddress + index - 1), 1).ConfigureAwait(false))[0];

                var measurementRange = _defaultMeasurementConfig;

                if (_configMap.TryGetValue(index, out var configVals))
                    measurementRange = configVals.MeasurementType;

                double data = DecodeRegisterValue(val);
                double currentData = ConvertAnalogInputDataToValue(data, _analogInputScalingValues[measurementRange]);

                output.Add(index, currentData);
            }

            return output;
        }

        override protected double DecodeRegisterValue(ushort registerValue)
        {
            bool isNegative = registerValue >> 15 == 1;
            var data = (registerValue & 0b_01111111_11111000) >> 3;
            data = isNegative ? -data : data;

            return data;
        }

    }

    /// <summary>
    /// p/n: PhoenixAnalogInputModule_2742748 id code 95 8 channel differential analog input 
    /// </summary>
    public class PhoenixAnalogInputModule_2742748 : PhoenixAnalogInputModule
    {
        private readonly SampleAverageAmount _defaultSampleAvgConfig;
        private readonly AnalogMeasurementRange _defaultMeasurementConfig;

        private readonly IReadOnlyDictionary<AnalogMeasurementRange, AnalogInputScaling> _analogInputScalingValues = new Dictionary<AnalogMeasurementRange, AnalogInputScaling>()
        {
            {AnalogMeasurementRange.ZeroToTwentyMilliamps, new AnalogInputScaling(0, 20, 0, 30000)},
            {AnalogMeasurementRange.FourToTwentyMilliamps, new AnalogInputScaling(4, 20, 0, 30000)},
            {AnalogMeasurementRange.ZeroToFortyMilliamps, new AnalogInputScaling(0, 40, 0, 30000)},
            {AnalogMeasurementRange.PlusMinusTwentyMilliamps, new AnalogInputScaling(-20, 20, 0, 30000)},
            {AnalogMeasurementRange.PlusMinusFortyMilliamps, new AnalogInputScaling(-40, 40, 0, 30000)}
        };

        public PhoenixAnalogInputModule_2742748(
            IModbusMaster modbusServ,
            ushort dataInStartAddress,
            ushort dataOutStartAddress,
            ushort dataOutWordCount,
            SampleAverageAmount defaultSampleAvgConfig = SampleAverageAmount.Four,
            AnalogMeasurementRange defaultMeasurementConfig = AnalogMeasurementRange.FourToTwentyMilliamps)
            : base(modbusServ, dataInStartAddress, dataOutStartAddress, dataOutWordCount)
        {
            _defaultMeasurementConfig = defaultMeasurementConfig;
            _defaultSampleAvgConfig = defaultSampleAvgConfig;
        }

        public override ushort GetNumberOfIOPoints() => 8;

        public override async Task InitializeAsync(TimeSpan initTimeout, CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;

            //configure all channels command
            ushort commandWordBase = 0b01000000_00000000;

            for (ushort i = 0; i < GetNumberOfIOPoints(); i++)
            {
                ushort commandWord = (ushort)(commandWordBase | (i << 8));

                //send configure command
                await WriteCommandUntilMirrored(commandWord, initTimeout - (DateTime.UtcNow - startTime), ct);

                ushort readConfig = ushort.MaxValue;

                var sw = new Stopwatch();
                sw.Start();

                var sampleAvg = _defaultSampleAvgConfig;
                var measurementRange = _defaultMeasurementConfig;

                if (_configMap.TryGetValue((ushort)(i + 1), out var config))
                {
                    sampleAvg = config.SampleAverageAmount;
                    measurementRange = config.MeasurementType;
                }

                var configWord = BuildConfigurationWord(sampleAvg, measurementRange);

                while (readConfig != configWord)
                {
                    //plus 1 since first word is command word
                    await _modbusServ.WriteSingleRegisterAsync(0, (ushort)(_dataOutStartAddress + 1), configWord);
                    readConfig = (await _modbusServ.ReadInputRegistersAsync(0,(ushort)(_dataInStartAddress + 1), 1))[0];

                    if (sw.Elapsed > initTimeout)
                        throw new TimeoutException("Timed out waiting for specified config to match read config.");
                }
            }
        }

        public override async Task<IReadOnlyDictionary<ushort, double>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            var output = new Dictionary<ushort, double>();

            foreach (var index in indexes)
            {
                //take measurement command with current index selection
                ushort measurementCmd = (ushort)((index - 1) << 8);

                await WriteCommandUntilMirrored(measurementCmd, TimeSpan.FromSeconds(3), ct);

                var val =
                    (await _modbusServ.ReadInputRegistersAsync(0, (ushort)(_dataInStartAddress + 1), 1).ConfigureAwait(false))[0];

                var measurementRange = _defaultMeasurementConfig;

                if (_configMap.TryGetValue(index, out var configVals))
                    measurementRange = configVals.MeasurementType;

                double data = DecodeRegisterValue(val); // Use base function for 15 bit IB IL
                double currentData = ConvertAnalogInputDataToValue(data, _analogInputScalingValues[measurementRange]);

                output.Add(index, currentData);
            }

            return output;
        }

        private ushort BuildConfigurationWord(
            SampleAverageAmount sampleAvgAmount,
            AnalogMeasurementRange currentMeasurementRange)
        {
            int val = 0;

            switch (sampleAvgAmount)
            {
                case SampleAverageAmount.Four:
                    val |= 0b_00000010_00000000;
                    break;
                case SampleAverageAmount.Sixteen:
                    break;
                case SampleAverageAmount.ThirtyTwo:
                    val |= 0b_00000011_00000000;
                    break;
                default:
                    throw new Exception("Failed to build module configuration word. Sample average count not recognized");
            }

            switch (currentMeasurementRange)
            {
                case AnalogMeasurementRange.ZeroToTwentyMilliamps:
                    val |= 0b_00000000_00001000;
                    break;
                case AnalogMeasurementRange.FourToTwentyMilliamps:
                    val |= 0b_00000000_00001010;
                    break;
                case AnalogMeasurementRange.PlusMinusTwentyMilliamps:
                    val |= 0b_00000000_00001001;
                    break;
                case AnalogMeasurementRange.ZeroToFortyMilliamps:
                    val |= 0b_00000000_00001100;
                    break;
                case AnalogMeasurementRange.PlusMinusFortyMilliamps:
                    val |= 0b_00000000_00001101;
                    break;
                default:
                    throw new Exception("Failed to build module configuration. Measurement range not recognized");
            }

            return (ushort)val;
        }
    }

    /// <summary>
    /// p/n: 2878447 id code 223 4 channel differential analog input module compatible with 2, 3, and 4 wire sensors and 
    /// configurable to 0-20ma, 4-20ma, +-20ma, 0-10V, +-10V, 0-5V, +-5V
    /// </summary>
    public class PhoenixAnalogInputModule_2878447 : PhoenixAnalogInputModule
    {
        protected readonly ushort _dataInWordCount;
        private readonly SampleAverageAmount _defaultSampleAvgConfig;
        private readonly AnalogMeasurementRange _defaultMeasurementConfig;

        private readonly IReadOnlyDictionary<AnalogMeasurementRange, AnalogInputScaling> _analogInputScalingValues = new Dictionary<AnalogMeasurementRange, AnalogInputScaling>()
        {
            {AnalogMeasurementRange.ZeroToTwentyMilliamps, new AnalogInputScaling(0, 20, 0, 30000)},
            {AnalogMeasurementRange.FourToTwentyMilliamps, new AnalogInputScaling(4, 20, 0, 30000)},
            {AnalogMeasurementRange.PlusMinusFiveVolts, new AnalogInputScaling(-5, 5, -30000, 30000)},
            {AnalogMeasurementRange.PlusMinusTenVolts, new AnalogInputScaling(-10, 10, -30000, 30000)},
            {AnalogMeasurementRange.PlusMinusTwentyMilliamps, new AnalogInputScaling(-20, 20, -30000, 30000)},
            {AnalogMeasurementRange.ZeroToFiveVolts, new AnalogInputScaling(0, 5, 0, 30000)},
            {AnalogMeasurementRange.ZeroToTenVolts, new AnalogInputScaling(0, 10, 0, 30000)}
        };

        public PhoenixAnalogInputModule_2878447(
            IModbusMaster modbusServ,
            ushort dataInStartAddress,
            ushort dataOutStartAddress,
            ushort dataInWordCount,
            ushort dataOutWordCount,
            SampleAverageAmount defaultSampleAvgConfig = SampleAverageAmount.Sixteen,
            AnalogMeasurementRange defaultMeasurementConfig = AnalogMeasurementRange.FourToTwentyMilliamps)
            : base(modbusServ, dataInStartAddress, dataOutStartAddress, dataOutWordCount)
        {
            _dataInWordCount = dataInWordCount;

            _defaultMeasurementConfig = defaultMeasurementConfig;
            _defaultSampleAvgConfig = defaultSampleAvgConfig;
        }

        // a word is one-to-one with a analog value
        public override ushort GetNumberOfIOPoints() => _dataInWordCount;

        public override async Task InitializeAsync(TimeSpan initTimeout, CancellationToken ct = default)
        {
            var startTime = DateTime.UtcNow;

            //configure channels command
            ushort configCommandWord = 0b01000000_00000000;

            //put into configure mode
            await WriteCommandUntilMirrored(configCommandWord, initTimeout - (DateTime.UtcNow - startTime));

            //plus 1 since first word is command word
            for (ushort i = 1; i < _dataInWordCount; i++)
            {
                ushort readConfig = ushort.MaxValue;
                var measurementRange = _defaultMeasurementConfig;
                var sampleAvg = _defaultSampleAvgConfig;

                if (_configMap.TryGetValue(i, out var configVals))
                {
                    measurementRange = configVals.MeasurementType;
                    sampleAvg = configVals.SampleAverageAmount;
                }

                var config = BuildConfigurationWord(sampleAvg, measurementRange);

                while (readConfig != config)
                {
                    //write configuration for current channel index
                    await _modbusServ.WriteSingleRegisterAsync(0, (ushort)(_dataOutStartAddress + i), config);
                    //read back status
                    var status = (await _modbusServ.ReadInputRegistersAsync(0, _dataInStartAddress, 1))[0];

                    if (status >> 15 == 1)
                        throw new Exception("Status register indicated error");

                    //send command to read config back
                    ushort readConfigCommand = (ushort)((0b00010000 | i - 1) << 8);
                    await WriteCommandUntilMirrored(readConfigCommand, initTimeout - (DateTime.UtcNow - startTime));

                    //read config back; this will always be in IN word 2
                    readConfig = (await _modbusServ.ReadInputRegistersAsync(0, (ushort)(_dataInStartAddress + 1), 1))[0];

                    await WriteCommandUntilMirrored(configCommandWord, initTimeout - (DateTime.UtcNow - startTime));
                }
            }

            //send read analog value command; continually reads value so no need to send each time
            await _modbusServ.WriteSingleRegisterAsync(0, _dataOutStartAddress, 0);
        }

        public override async Task<IReadOnlyDictionary<ushort, double>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            var output = new Dictionary<ushort, double>();

            foreach (var index in indexes)
            {
                var val =
                    //no minus 1 since first IN word is echo of OUT command word
                    (await _modbusServ.ReadInputRegistersAsync(0, (ushort)(_dataInStartAddress + index), 1).ConfigureAwait(false))[0];

                double data = DecodeRegisterValue(val); // Use base function for 15 bit IB IL

                var measurementRange = _defaultMeasurementConfig;

                if (_configMap.TryGetValue(index, out var configVals))
                    measurementRange = configVals.MeasurementType;

                double currentData = ConvertAnalogInputDataToValue(data, _analogInputScalingValues[measurementRange]);

                output.Add(index, currentData);
            }

            return output;
        }

        private ushort BuildConfigurationWord(
            SampleAverageAmount sampleAvgAmount,
            AnalogMeasurementRange currentMeasurementRange)
        {
            int val = 0;

            switch (sampleAvgAmount)
            {
                case SampleAverageAmount.Four:
                    val |= 0b_00000010_00000000;
                    break;
                case SampleAverageAmount.Sixteen:
                    break;
                case SampleAverageAmount.ThirtyTwo:
                    val |= 0b_00000011_00000000;
                    break;
                default:
                    throw new Exception("Failed to build module configuration word. Sample average count not recognized");
            }

            switch (currentMeasurementRange)
            {
                case AnalogMeasurementRange.ZeroToTwentyMilliamps:
                    val |= 0b_00000000_00001000;
                    break;
                case AnalogMeasurementRange.FourToTwentyMilliamps:
                    val |= 0b_00000000_00001010; 
                    break;
                case AnalogMeasurementRange.PlusMinusTwentyMilliamps:
                    val |= 0b_00000000_00001001;
                    break;
                case AnalogMeasurementRange.ZeroToTenVolts:
                    break;
                case AnalogMeasurementRange.ZeroToFiveVolts:
                    val |= 0b_00000000_00000010;
                    break;
                case AnalogMeasurementRange.PlusMinusTenVolts:
                    val |= 0b_00000000_00000001;
                    break;
                case AnalogMeasurementRange.PlusMinusFiveVolts:
                    val |= 0b_00000000_00000011;
                    break;
                default:
                    throw new Exception("Failed to build module configuration. Measurement range not recognized");
            }
            return (ushort)val;
        }
    }

    public interface IAnalogOutputModule : IIOModule
    {
        Task InitializeAsync(TimeSpan initTimeout, CancellationToken ct = default);
        Task WriteAsync(ushort index, double value, CancellationToken ct = default);
        Task WriteManyAsync(IReadOnlyDictionary<ushort, double> values, CancellationToken ct = default);
        Task<IReadOnlyDictionary<ushort, double>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default);

        void SetOutputConfiguration(IReadOnlyDictionary<ushort, AnalogMeasurementRange> configMap);
    }

    /// <summary>
    /// P/N 2700775; id code: 91   2 channel analog output 
    /// </summary>
    public class PhoenixAnalogOutputModule_2700775 : IAnalogOutputModule
    {

        private IReadOnlyDictionary<ushort, AnalogMeasurementRange> _configMap;
        private readonly IModbusMaster _modbusServ;

        private readonly AnalogMeasurementRange _defaultMeasurementTypeConfig;

        private readonly ushort _dataInStartAddress;
        private readonly ushort _dataOutStartAddress;

        private const double _minVoltage = 0;
        private const double _maxVoltage = 10.837;
        private const double _minCurrent = 4;
        private const double _maxCurrent = 21.339;
        private const double _minRegVal = 0;
        private const double _maxRegVal = 32512;

        public PhoenixAnalogOutputModule_2700775(
            IModbusMaster modbusServ,
            ushort dataInStartAddress,
            ushort dataOutStartAddress,
            AnalogMeasurementRange defaultMeasurementTypeConfig = AnalogMeasurementRange.ZeroToTenVolts)
        {
            _dataInStartAddress = dataInStartAddress;
            _dataOutStartAddress = dataOutStartAddress;

            _modbusServ = modbusServ;
            _defaultMeasurementTypeConfig = defaultMeasurementTypeConfig;
        }

        private ushort ConvertTargetValueToRegisterVal(
            double value,
            AnalogMeasurementRange outputType)
        {
            double output;

            if (outputType == AnalogMeasurementRange.ZeroToTenVolts)
            {
                if (value > 10)
                    throw new ArgumentOutOfRangeException($"Failed to convert register value to voltage. The value specified: {value} was greater than max: {10}");
                if (value < 0)
                    throw new ArgumentOutOfRangeException($"Failed to convert register value to voltage. The value specified: {value} was less than min: {0}");

                output = RangeMapper.MapValueToRange(value, _minVoltage, _maxVoltage, _minRegVal, _maxRegVal);
            }
            else if (outputType == AnalogMeasurementRange.FourToTwentyMilliamps)
            {
                if (value > 20)
                    throw new ArgumentOutOfRangeException($"Failed to convert register value to current. The value specified: {value} was greater than max: {20}");
                if (value < 4)
                    throw new ArgumentOutOfRangeException($"Failed to convert register value to current. The value specified: {value} was less than min: {4}");

                output = RangeMapper.MapValueToRange(value, _minCurrent, _maxCurrent, _minRegVal, _maxRegVal);
            }
            else
            {
                throw new Exception("Currently only 4-20mA and 0-10V supported");
            }

            var finalOutput = (Convert.ToUInt16(Math.Round(output)) & 0b01111111_11110000);
            return (ushort)finalOutput;
        }

        private double ConvertRegisterValueToValue(
            double regVal,
            AnalogMeasurementRange outputType)
        {
            double output;

            if (outputType == AnalogMeasurementRange.ZeroToTenVolts)
            {
                if (regVal > 32512)
                    throw new ArgumentOutOfRangeException($"Failed to convert register value to voltage. The value specified: {regVal} was greater than max: {32512}");
                if (regVal < 0)
                    throw new ArgumentOutOfRangeException($"Failed to convert register value to voltage. The value specified: {regVal} was less than min: {0}");

                output = RangeMapper.MapValueToRange(regVal, _minRegVal, _maxRegVal, _minVoltage, _maxVoltage);
            }
            else if (outputType == AnalogMeasurementRange.FourToTwentyMilliamps)
            {
                if (regVal > 32512)
                    throw new ArgumentOutOfRangeException($"Failed to convert register value to current. The value specified: {regVal} was greater than max: {32512}");
                if (regVal < 0)
                    throw new ArgumentOutOfRangeException($"Failed to convert register value to current. The value specified: {regVal} was less than min: {0}");

                output = RangeMapper.MapValueToRange(regVal, _minRegVal, _maxRegVal, _minCurrent, _maxCurrent);
            }
            else
            {
                throw new Exception("Currently only 4-20mA and 0-10V supported");
            }

            return output;
        }

        protected async Task WriteCommandUntilMirrored(
             ushort value,
             ushort inAddress,
             ushort outAddress,
             TimeSpan timeout,
             CancellationToken ct = default)
        {
            ushort readVal = ushort.MaxValue;
            var startTime = DateTime.UtcNow;
            var maskedVal = value & 0b00000000_00111111;

            while (readVal != maskedVal)
            {
                await _modbusServ.WriteSingleRegisterAsync(0, outAddress, value);
                readVal = (await _modbusServ.ReadInputRegistersAsync(0, inAddress, 1))[0];
                readVal &= 0b00000000_00111111;

                if ((DateTime.UtcNow - startTime) > timeout)
                {
                    readVal = (await _modbusServ.ReadInputRegistersAsync(0, inAddress, 1))[0];

                    if (readVal >> 15 == 1)
                    {
                        throw new Exception(
                            $"Timed out while waiting for mirrored command to match written command. " +
                            $"Status register indicated error. Status word : {readVal}" +
                            $"Written value: {value}. Read value: {readVal}");
                    }

                    throw new Exception("Timed out while waiting for mirrored command to match written command");
                }
            }
        }

        private ushort BuildConfigurationWord(
            AnalogMeasurementRange measurementType)
        {
            bool isFourTwenty = measurementType == AnalogMeasurementRange.FourToTwentyMilliamps;
            if (measurementType != AnalogMeasurementRange.ZeroToTenVolts && !isFourTwenty)
                throw new Exception(
                        $"Error building configuration word. Currently only support 0-10V and 4-20mA. Specified type: {measurementType}");

            var output = 0b_10000000_00000000;

            if (isFourTwenty)
                output |= 0b_00000000_00001010;

            return (ushort)output;
        }

        public ushort GetNumberOfIOPoints() => 2;

        private double DecodeRegisterBits(
            ushort regVal)
        {
            bool isNegative = regVal >> 15 == 1;
            var val = (regVal & 0b_01111111_11110000);
            val = isNegative ? -val : val;
            return val;
        }

        public async Task<IReadOnlyDictionary<ushort, double>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            var indexHash = new HashSet<ushort>(indexes);
            var addr = (ushort)(_dataInStartAddress + 2);
            var regVals = await _modbusServ.ReadInputRegistersAsync(0, addr, 2);

            var filtered =
                regVals
                .Select((x, i) => (address: addr + i, value: x))
                .Where(x => indexHash.Contains((ushort)(x.address - _dataInStartAddress - 1)))
                .ToDictionary(
                    x => (ushort)(x.address - _dataInStartAddress - 1),
                    x =>
                    {
                        var outputType = _defaultMeasurementTypeConfig;
                        if (_configMap.TryGetValue((ushort)(x.address - _dataInStartAddress - 1), out var measurementType))
                            outputType = measurementType;

                        return ConvertRegisterValueToValue(DecodeRegisterBits(x.value), outputType);
                    });

            return filtered;
        }

        public Task WriteAsync(ushort index, double value, CancellationToken ct = default)
        {
            //+2 because first two out words are configuration, + index to set correct output, -1 because 0 indexed
            return _modbusServ.WriteSingleRegisterAsync(0, (ushort)(_dataOutStartAddress + 2 + index - 1), ConvertTargetValueToRegisterVal(value, _configMap[index]));
        }

        public async Task WriteManyAsync(IReadOnlyDictionary<ushort, double> values, CancellationToken ct = default)
        {
            foreach (var item in values)
            {
                await WriteAsync(item.Key, item.Value, ct);
            }
        }

        public async Task InitializeAsync(TimeSpan initTimeout, CancellationToken ct = default)
        {
            for (ushort i = 0; i < 2; i++)
            {
                var measurementRange = _defaultMeasurementTypeConfig;

                if (_configMap.TryGetValue(i, out var measureType))
                    measurementRange = measureType;

                var configWord = BuildConfigurationWord(measurementRange);
                await WriteCommandUntilMirrored(configWord, (ushort)(_dataInStartAddress + i), (ushort)(_dataOutStartAddress + i), TimeSpan.FromSeconds(3), ct);
            }
        }

        public void SetOutputConfiguration(IReadOnlyDictionary<ushort, AnalogMeasurementRange> configMap)
        {
            _configMap = configMap;
        }
    }

    public class PhoenixAnalogOutputModule_2702497 : IAnalogOutputModule
    {
        private readonly ushort _dataOutStartAddress;
        private readonly IModbusMaster _modbusServ;

        private const double _minCurrent = 4;
        private const double _maxCurrent = 20;

        private const double _minRegVal = 0;
        private const double _maxRegVal = 16000;


        public PhoenixAnalogOutputModule_2702497(
            ushort dataOutStartAddress,
            IModbusMaster modbusServ)
        {
            _dataOutStartAddress = dataOutStartAddress;
            _modbusServ = modbusServ;
        }

        public ushort GetNumberOfIOPoints() => 4;

        private ushort ConvertTargetCurrentToRegisterVal(
            double current)
        {
            if (current > _maxCurrent)
                throw new ArgumentOutOfRangeException($"Failed to convert current to register value. The current specified: {current} was greater than max: {_maxCurrent}");
            if (current < _minCurrent)
                throw new ArgumentOutOfRangeException($"Failed to convert current to register value. The current specified: {current} was less than min: {_minCurrent}");

            var output = Convert.ToUInt16(Math.Round(RangeMapper.MapValueToRange(current, _minCurrent, _maxCurrent, _minRegVal, _maxRegVal)));

            return output;
        }

        private double ConvertRegisterValueToCurrent(
            double regVal)
        {
            if (regVal > _maxRegVal)
                throw new ArgumentOutOfRangeException($"Failed to convert register value to current. The value specified: {regVal} was greater than max: {_maxRegVal}");
            if (regVal < _minRegVal)
                throw new ArgumentOutOfRangeException($"Failed to convert register value to current. The value specified: {regVal} was less than min: {_minRegVal}");

            var output = Convert.ToUInt16(Math.Round(RangeMapper.MapValueToRange(regVal, _minRegVal, _maxRegVal, _minCurrent, _maxCurrent)));

            return output;
        }

        public async Task WriteManyAsync(IReadOnlyDictionary<ushort, double> values, CancellationToken ct = default)
        {
            foreach (var kvp in values)
                await _modbusServ.WriteSingleRegisterAsync(0, (ushort)(_dataOutStartAddress + kvp.Key - 1), ConvertTargetCurrentToRegisterVal(kvp.Value));
        }

        public Task WriteAsync(ushort index, double value, CancellationToken ct = default)
        {
            return WriteManyAsync(new Dictionary<ushort, double>() { { index, value } });
        }

        public async Task<IReadOnlyDictionary<ushort, double>> ReadManyAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            var output = new Dictionary<ushort, double>();

            foreach (var index in indexes)
            {
                var regVal = (await _modbusServ.ReadInputRegistersAsync(0, (ushort)(_dataOutStartAddress + index - 1), 1))[0];
                output[index] = ConvertRegisterValueToCurrent(regVal);
            }

            return output;
        }

        public Task InitializeAsync(TimeSpan initTimeout, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public void SetOutputConfiguration(IReadOnlyDictionary<ushort, AnalogMeasurementRange> configMap)
        {
            if (configMap.Any(x => x.Value != AnalogMeasurementRange.FourToTwentyMilliamps))
                throw new Exception(
                    "Failed to set output configuration. This module only supports 4-20mA");

            return;
        }
    }


    /// <summary>
    /// Represents aggregate of IO sources
    /// </summary>
    public interface IIOBus
    {
        Task WriteDigitalOutputsAsync(IReadOnlyDictionary<ushort, bool> states, CancellationToken ct = default);
        Task WriteDigitalOutputAsync(ushort index, bool state, CancellationToken ct = default);
        Task<Dictionary<ushort, bool>> ReadDigitalOutputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default);
        Task<bool> ReadDigitalOutputAsync(ushort index, CancellationToken ct = default);


        Task<Dictionary<ushort, bool>> ReadDigitalInputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default);
        Task<bool> ReadDigitalInputAsync(ushort index, CancellationToken ct = default);
        Task WriteAnalogOutputAsync(ushort index, double value, CancellationToken ct = default);
        Task WriteAnalogOutputsAsync(IReadOnlyDictionary<ushort, double> values, CancellationToken ct = default);
        Task<IReadOnlyDictionary<ushort, double>> ReadAnalogOutputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default);

        Task<double> ReadAnalogInputAsync(ushort index, CancellationToken ct = default);
        Task<IReadOnlyDictionary<ushort, double>> ReadAnalogInputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default);
    }

    /// <summary>
    /// Represents an ephemeral connection to and aggregate of IO sources 
    /// </summary>
    public interface IDisposableIOBus : IIOBus, IDisposableConnection
    {

    }

    public interface IDisposableConnection : IDisposable
    {
        Task<bool> IsConnectedAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// Represents an ephemeral connection to an aggregate of IO sources presented as if contiguous IO. 
    /// These IO can be on same device or not and can use the same communication protocol or not
    /// </summary>
    public class IOBus : IDisposableIOBus
    {
        private readonly IModbusMaster _connection;

        private readonly DigitalInputModulesActionsModel _digitalInputActionsModel;
        private readonly DigitalOutputModulesActionsModel _digitalOutputActionsModel;
        private readonly AnalogInputModulesActionsModel _analogInputActionsModel;
        private readonly AnalogOutputModulesActionsModel _analogOutputActionsModel;

        public IOBus(
            IModbusMaster master,
            DigitalInputModulesActionsModel digitalInputActionsModel,
            DigitalOutputModulesActionsModel digitalOutputActionsModel,
            AnalogInputModulesActionsModel analogInputActionsModel,
            AnalogOutputModulesActionsModel analogOutputActionsModel)
        {
            _digitalInputActionsModel = digitalInputActionsModel;
            _digitalOutputActionsModel = digitalOutputActionsModel;
            _analogInputActionsModel = analogInputActionsModel;
            _analogOutputActionsModel = analogOutputActionsModel;

            _connection = master;
        }

        private bool _isDisposed;


        #region INTERNAL_METHODS

        protected IReadOnlyDictionary<ushort, List<ushort>> BuildModuleBasedIOIndexToBusBasedIndexMap(
            IEnumerable<ushort> indexes,
            IModulesIoIndexModel moduleIoIndexMaps)
        {
            //build a dictionary where module io indexes are grouped by module id. O(n)
            var idGrouping = new Dictionary<ushort, List<ushort>>();
            string failPrefix = "Failed to build module index to IO index. ";

            foreach (var index in indexes)
            {
                bool found = moduleIoIndexMaps.BusBasedIndexToModuleIdMap.TryGetValue(index, out ushort moduleId);
                if (!found)
                    throw new KeyNotFoundException($"{failPrefix} No module id associated with IO index: {index}");

                found = moduleIoIndexMaps.BusBasedIndexToModuleIndexMap.TryGetValue(index, out ushort moduleBasedIOIndex);
                if (!found)
                    throw new KeyNotFoundException($"{failPrefix} No module IO index associated with bus IO index: {index}");

                bool moduleFound = idGrouping.TryGetValue(moduleId, out var ioIndexes);

                if (moduleFound)
                    ioIndexes.Add(moduleBasedIOIndex);
                else
                    idGrouping.Add(moduleId, new List<ushort>() { moduleBasedIOIndex });
            }

            return idGrouping;
        }

        protected IReadOnlyDictionary<ushort, List<ushort>> BuildDigitalInputModuleIndexToIOIndexMap(
            IEnumerable<ushort> indexes,
            IDigitalModulesActionsModel actionsModel)
        {
            //build a dictionary where module io indexes are grouped by module id. O(n)
            var idGrouping = BuildModuleBasedIOIndexToBusBasedIndexMap(indexes, actionsModel);

            return idGrouping;
        }

        protected IReadOnlyDictionary<ushort, List<ushort>> BuildDigitalOutputModuleBasedIOIndexToBusBasedIOIndexMap(
            IEnumerable<ushort> indexes,
            IDigitalModulesActionsModel actionsModel)
        {
            //build a dictionary where module io indexes are grouped by module id. O(n)
            var idGrouping = BuildModuleBasedIOIndexToBusBasedIndexMap(indexes, actionsModel);

            return idGrouping;
        }

        protected IReadOnlyDictionary<ushort, List<ushort>> BuildAnalogModuleBasedIOIndexToBusBasedIOIndexMap(
            IEnumerable<ushort> indexes,
            IAnalogModulesActionsModel actionsModel)
        {
            return BuildModuleBasedIOIndexToBusBasedIndexMap(indexes, actionsModel);
        }

        protected IReadOnlyDictionary<ushort, Dictionary<ushort, T>> BuildModuleBasedIndexToBusBasedIOStateMap<T>(
            IReadOnlyDictionary<ushort, T> points,
            IReadOnlyDictionary<ushort, ushort> busBasedIndexToModuleIdMap,
            IReadOnlyDictionary<ushort, ushort> busBasedIndexToModuleIndexMap)
        {
            //build a dictionary where module io indexes are grouped by module id
            var idGrouping = new Dictionary<ushort, Dictionary<ushort, T>>();
            foreach (var point in points)
            {
                if (!busBasedIndexToModuleIdMap.TryGetValue(point.Key, out ushort modId))
                    throw new KeyNotFoundException(
                        $"Failed to get module id from specified IO index: {point.Key}. " +
                        $"Outside the range of configured IO points. Configured indexes: {string.Join(",", busBasedIndexToModuleIdMap.Select(x => x.Key))}");

                if (!busBasedIndexToModuleIndexMap.TryGetValue(point.Key, out ushort moduleIOIndex))
                    throw new ArgumentOutOfRangeException(
                        $"Failed to get module index from specified IO index: {point.Key}" +
                        $"Outside the range of configured IO points. Configured indexes: {string.Join(",", busBasedIndexToModuleIndexMap.Select(x => x.Key))}");

                bool moduleFound = idGrouping.TryGetValue(modId, out var ioIndexes);

                if (moduleFound)
                    ioIndexes.Add(moduleIOIndex, point.Value);
                else
                    idGrouping.Add(modId, new Dictionary<ushort, T>() { { moduleIOIndex, point.Value } });
            }

            return idGrouping;
        }

        protected async Task<Dictionary<ushort, bool>> ReadManyDigitalIOAsync(
            IEnumerable<ushort> indexes,
            IDigitalModulesActionsModel digitalModuleActionsModel,
            CancellationToken ct = default)
        {
            string failPrefix = "Failed to read many digital IO points. ";

            var idGrouping = BuildDigitalInputModuleIndexToIOIndexMap(indexes, digitalModuleActionsModel);

            //iterate over module groups and read
            var outputTasks =
                idGrouping
                .Select(async kvp =>
                {
                    //try to get read action
                    bool found = digitalModuleActionsModel.ModuleBasedIndexToReadActionMap.TryGetValue(kvp.Key, out var modAction);
                    if (!found)
                        throw new KeyNotFoundException($"{failPrefix} No module with id: {kvp.Key} found.");

                    //transform back to one-based bus index
                    var output =
                        (await modAction(kvp.Value, ct))
                        .ToDictionary(x =>
                        {
                            //try to get one-based bus index
                            bool tempFound = digitalModuleActionsModel.ModuleInfoToBusBasedIndexMap.TryGetValue(new ModuleIdIOIndexPair(kvp.Key, x.Key), out ushort busBasedIndex);

                            if (!tempFound)
                                throw new KeyNotFoundException($"{failPrefix} No bus index associated with module index: {kvp.Key} and module IO index: {x.Key}.");

                            return busBasedIndex;

                        },
                        x => x.Value);

                    return output;
                });

            //calls are parallelized to different modules, merge into flat collection
            var points = (await Task.WhenAll(outputTasks)).SelectMany(p => p).ToDictionary(x => x.Key, x => x.Value);

            return points;
        }

        #endregion


        #region IIOBus

        public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
        {
            //READ SPECIFIC REGISTER HERE
            try
            {
                await _connection.ReadInputRegistersAsync(0, 1400, 1);
                return true;
            }
            catch (Exception)
            {
                return false;
            } 
        }

        public async Task<double> ReadAnalogInputAsync(ushort index, CancellationToken ct = default)
        {
            return (await ReadAnalogInputsAsync(Enumerable.Repeat(index, 1), ct)).First().Value;
        }

        public async Task<IReadOnlyDictionary<ushort, double>> ReadAnalogInputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            string failPrefix = "Failed to read many analog IO points. ";

            var idGrouping = BuildAnalogModuleBasedIOIndexToBusBasedIOIndexMap(indexes, _analogInputActionsModel);

            //iterate over module groups and read
            var outputTasks =
                idGrouping
                .Select(async kvp =>
                {
                    //try to get read action
                    bool found = _analogInputActionsModel.ModuleBasedIndexToReadActionMap.TryGetValue(kvp.Key, out var modAction);
                    if (!found)
                        throw new KeyNotFoundException($"{failPrefix} No module with id: {kvp.Key} found.");

                    //transform back to one-based bus index
                    var output =
                        (await modAction(kvp.Value, ct))
                        .ToDictionary(x =>
                        {
                            //try to get one-based bus index
                            bool tempFound = _analogInputActionsModel.ModuleInfoToBusBasedIndexMap.TryGetValue(new ModuleIdIOIndexPair(kvp.Key, x.Key), out ushort busBasedIndex);

                            if (!tempFound)
                                throw new KeyNotFoundException($"{failPrefix} No bus index associated with module index: {kvp.Key} and module IO index: {x.Key}.");

                            return busBasedIndex;

                        },
                        x => x.Value);

                    return output;
                });

            //calls are parallelized to different modules, merge into flat collection
            var points = (await Task.WhenAll(outputTasks)).SelectMany(p => p).ToDictionary(x => x.Key, x => x.Value);

            return points;
        }

        public Task WriteAnalogOutputsAsync(IReadOnlyDictionary<ushort, double> values, CancellationToken ct = default)
        {
            string failPrefix = "Failed to read many analog IO points. ";

            var idGrouping =
                BuildModuleBasedIndexToBusBasedIOStateMap(
                   values,
                   _analogOutputActionsModel.BusBasedIndexToModuleIdMap,
                   _analogOutputActionsModel.BusBasedIndexToModuleIndexMap);

            //iterate over module groups and read
            var outputTasks =
                idGrouping
                .Select(kvp =>
                {
                    //try to get write action
                    bool found = _analogOutputActionsModel.ModuleBasedIndexToWriteActionMap.TryGetValue(kvp.Key, out var modAction);
                    if (!found)
                        throw new KeyNotFoundException($"{failPrefix} No module with id: {kvp.Key} found.");

                    var output = modAction(kvp.Value, ct);
                    return output;
                });

            return Task.WhenAll(outputTasks);
        }

        public Task WriteAnalogOutputAsync(ushort index, double value, CancellationToken ct = default)
        {
            return WriteAnalogOutputsAsync(new Dictionary<ushort, double>() { { index, value } }, ct);
        }

        public async Task<IReadOnlyDictionary<ushort, double>> ReadAnalogOutputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            string failPrefix = "Failed to read many analog IO points. ";

            var idGrouping = BuildAnalogModuleBasedIOIndexToBusBasedIOIndexMap(indexes, _analogOutputActionsModel);

            //iterate over module groups and read
            var outputTasks =
                idGrouping
                .Select(kvp =>
                {
                    //try to get read action
                    bool found = _analogOutputActionsModel.ModuleBasedIndexToReadActionMap.TryGetValue(kvp.Key, out var modAction);
                    if (!found)
                        throw new KeyNotFoundException($"{failPrefix} No module with id: {kvp.Key} found.");

                    var output = modAction(kvp.Value, ct);
                    return output;
                });

            var outputState = (await Task.WhenAll(outputTasks)).SelectMany(x => x).ToDictionary(x => x.Key, x => x.Value);
            return outputState;
        }


        public async Task<bool> ReadDigitalInputAsync(ushort index, CancellationToken ct = default)
        {
            bool found = _digitalInputActionsModel.BusBasedIndexToModuleIdMap.TryGetValue(index, out ushort moduleIndex);

            string failPrefix = "Failed to read digital input. ";
            if (!found)
                throw new KeyNotFoundException();

            found = _digitalInputActionsModel.ModuleBasedIndexToReadActionMap.TryGetValue(moduleIndex, out var action);

            if (!found)
                throw new KeyNotFoundException($"{failPrefix} No digital input found with index: {index}");

            found = _digitalInputActionsModel.BusBasedIndexToModuleIndexMap.TryGetValue(index, out ushort modulePointIndex);

            if (!found)
                throw new KeyNotFoundException($"{failPrefix} No digital input module point index found for bus index: {index}");

            bool state = (await action(new List<ushort>() { modulePointIndex }, ct)).FirstOrDefault().Value;
            return state;
        }

        public async Task<Dictionary<ushort, bool>> ReadDigitalInputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            return await ReadManyDigitalIOAsync(indexes, _digitalInputActionsModel);
        }

        public async Task WriteDigitalOutputAsync(ushort index, bool state, CancellationToken ct = default)
        {
            bool found = _digitalOutputActionsModel.BusBasedIndexToModuleIdMap.TryGetValue(index, out var moduleIndex);
            string failPrefix = "Failed to write digital output. ";
            if (!found)
                throw new KeyNotFoundException($"{failPrefix} No module found for IO index: {index}");

            found = _digitalOutputActionsModel.ModuleWriteActionMap.TryGetValue(moduleIndex, out var outputAction);

            if (!found)
                throw new KeyNotFoundException($"{failPrefix} No output actions found for module with index: {index}");

            found = _digitalOutputActionsModel.BusBasedIndexToModuleIndexMap.TryGetValue(index, out ushort modulePointIndex);

            if (!found)
                throw new KeyNotFoundException($"{failPrefix} No digital output module point index found for bus index: {index}");

            await outputAction(new Dictionary<ushort, bool>() { { modulePointIndex, state } }, ct);
        }

        public async Task WriteDigitalOutputsAsync(
            IReadOnlyDictionary<ushort, bool> points,
            CancellationToken ct = default)
        {
            var idGrouping =
                BuildModuleBasedIndexToBusBasedIOStateMap(
                    points,
                    _digitalOutputActionsModel.BusBasedIndexToModuleIdMap,
                    _digitalOutputActionsModel.BusBasedIndexToModuleIndexMap);

            var tasks = new List<Task>();

            foreach (var kvp in idGrouping)
            {
                if (!_digitalOutputActionsModel.ModuleWriteActionMap.TryGetValue(kvp.Key, out var modAction))
                    throw new KeyNotFoundException(
                        $"Specified module index not configured on device: {kvp.Key}. " +
                        $"Available DIO modules by index: {string.Join(",", _digitalOutputActionsModel.ModuleWriteActionMap.Select(x => x.Key))}");

                tasks.Add(modAction(kvp.Value, ct));
            }

            await Task.WhenAll(tasks);
        }


        public async Task<Dictionary<ushort, bool>> ReadDigitalOutputsAsync(IEnumerable<ushort> indexes, CancellationToken ct = default)
        {
            return await ReadManyDigitalIOAsync(indexes, _digitalOutputActionsModel);
        }

        public async Task<bool> ReadDigitalOutputAsync(ushort index, CancellationToken ct = default)
        {
            bool found = _digitalOutputActionsModel.BusBasedIndexToModuleIdMap.TryGetValue(index, out ushort moduleId);
            string failPrefix = "Failed to read digital output";

            if (!found)
                throw new KeyNotFoundException($"{failPrefix} No module index associated with specified IO index: {index}");

            found = _digitalOutputActionsModel.ModuleBasedIndexToReadActionMap.TryGetValue(moduleId, out var moduleAction);

            if (!found)
                throw new KeyNotFoundException($"{failPrefix} No module read actions associated with specified module id: {moduleId}");

            found = _digitalOutputActionsModel.BusBasedIndexToModuleIndexMap.TryGetValue(index, out ushort moduleIndex);

            if (!found)
                throw new KeyNotFoundException($"{failPrefix} No module based index found for bus index: {index}");

            var state = (await moduleAction(new List<ushort>() { moduleIndex }, ct));
            if (state == null || state.Count == 0)
                throw new NullReferenceException($"{failPrefix} Failed to get state of digital output: {index}");

            return state.FirstOrDefault().Value;
        }


        #endregion

        #region IDisposable

        protected void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _connection.Dispose();
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

    public interface ITcpIOBusConnectionFactory
    {
        Task<IDisposableIOBus> ConnectAsync(
            string ipAddress,
            TimeSpan connectionTimeout,
            TimeSpan connectionWatchdogInterval,
            IReadOnlyDictionary<ushort, AnalogInputConfig> analogInConfig,
            IReadOnlyDictionary<ushort, AnalogMeasurementRange> analogOutConfig,
            int port = 502,
            CancellationToken ct = default);
    }


    public struct ModuleData
    {
        public ModuleProcessDataType ModuleProcessDataType { get; }
        public IoType IoType { get; }
    }

    public struct ModuleCompositeAddress
    {
        public ModuleCompositeAddress(
            ushort moduleIndex,
            ushort ioIndex)
        {
            ModuleIndex = moduleIndex;
            IoIndex = ioIndex;
        }

        public ushort ModuleIndex { get; }
        public ushort IoIndex { get; }
    }

    public struct AnalogInputConfig
    {
        public AnalogInputConfig(
            AnalogMeasurementRange measurementType,
            SampleAverageAmount sampleAvgAmount)
        {
            MeasurementType = measurementType;
            SampleAverageAmount = sampleAvgAmount;
        }

        public AnalogMeasurementRange MeasurementType { get; }
        public SampleAverageAmount SampleAverageAmount { get; }
    }

    public class PhoenixIOBusConnectionFactory : ITcpIOBusConnectionFactory
    {
        //NEEDS TO BE LIST SINCE SAME MODULE CAN BE AN INPUT AND OUTPUT PROCESS DATA TYPE 
        //need these to determine address since inputs are assigned addresses prior to outputs in dynamic tables
        private static readonly IReadOnlyDictionary<int, (ModuleProcessDataType dataType, IoType iotype)> _moduleIdCodeDataTypeMap =
            new Dictionary<int, (ModuleProcessDataType dataType, IoType iotype)>()
            {
                { 190, (ModuleProcessDataType.Input, IoType.DigitalInput) },
                { 126, (ModuleProcessDataType.Input, IoType.DigitalInput) },
                { 125, (ModuleProcessDataType.Output, IoType.AnalogOutput) },
                { 91, (ModuleProcessDataType.InputOutput, IoType.AnalogOutput) },
                { 189, (ModuleProcessDataType.Output, IoType.DigitalOutput) },
                { 127, (ModuleProcessDataType.InputOutput, IoType.AnalogInput) },
                { 223, (ModuleProcessDataType.InputOutput, IoType.AnalogInput) },
                { 95, (ModuleProcessDataType.InputOutput, IoType.AnalogInput) }
            };

        private enum DataLengthUnits
        {
            Bit = 1,
            Nibble = 4,
            Byte = 8,
            Word = 16
        }



        /// <summary>
        /// This hard-coded encoding is defined by phoenix and can be found in IBS SYS FW G4 UM E 
        /// </summary>
        private static readonly IReadOnlyDictionary<int, DataLengthUnits> LengthIdToDataBitsMap =
            new Dictionary<int, DataLengthUnits>()
            {
                { 0, DataLengthUnits.Word },
                { 2, DataLengthUnits.Byte },
                { 1, DataLengthUnits.Nibble },
                { 3, DataLengthUnits.Bit },
            };

        private int DecodeDataBitsLength(byte dataLengthByte)
        {
            int lengthUnitId = dataLengthByte >> 6;
            int unitAmount = dataLengthByte & 0b_00111111;
            int dataBitsCount = (int)LengthIdToDataBitsMap[lengthUnitId] * unitAmount;
            return dataBitsCount;
        }

        private delegate IDigitalInputModule BuildDigInModule(IModbusMaster modbusServ, IoModuleInfo modInfo);

        private readonly IReadOnlyDictionary<byte, BuildDigInModule> _digitalInputModuleBuilder = new Dictionary<byte, BuildDigInModule>()
        {
            {
                190,
                (modbusMaster, moduleInfo) =>
                    new PhoenixDigitalInputModule(
                        modbusMaster,
                        moduleInfo.DataInStartAddress,
                        moduleInfo.DataBitCount,
                        moduleInfo.WordCount)
            }
        };

        private delegate IDigitalOutputModule BuildDigOutModule(IModbusMaster modbusServ, IoModuleInfo modInfo);

        private readonly IReadOnlyDictionary<byte, BuildDigOutModule> _digitalOutputModuleBuilder = new Dictionary<byte, BuildDigOutModule>()
        {
            {
                189,
                (modbusServ, moduleInfo) =>
                    new PhoenixDigitalOutputModule(
                        modbusServ,
                        moduleInfo.DataOutStartAddress,
                        moduleInfo.DataBitCount,
                        moduleInfo.WordCount)
            }
        };

        private delegate IAnalogInputModule BuildAnalogInModule(IModbusMaster modbusServ, IoModuleInfo modInfo);

        private readonly IReadOnlyDictionary<byte, BuildAnalogInModule> _analogInputModuleBuilder = new Dictionary<byte, BuildAnalogInModule>()
        {
            {
                127,
                (modbusServ, moduleInfo) =>
                    new PhoenixAnalogInputModule_2700458(
                        modbusServ,
                        moduleInfo.DataInStartAddress,
                        moduleInfo.DataOutStartAddress,
                        moduleInfo.WordCount,
                        moduleInfo.WordCount)
            },
            {
                223,
                (modbusServ, moduleInfo) =>
                    new PhoenixAnalogInputModule_2878447(
                        modbusServ,
                        moduleInfo.DataInStartAddress,
                        moduleInfo.DataOutStartAddress,
                        moduleInfo.WordCount,
                        moduleInfo.WordCount)
            },

            {
                95,
                (modbusServ, moduleInfo) =>
                    new PhoenixAnalogInputModule_2742748(
                        modbusServ,
                        moduleInfo.DataInStartAddress,
                        moduleInfo.DataOutStartAddress,
                        moduleInfo.WordCount)
            }
        };

        private delegate IAnalogOutputModule BuildAnalogOutModule(IModbusMaster modbusServ, IoModuleInfo modInfo);

        private readonly IReadOnlyDictionary<byte, BuildAnalogOutModule> _analogOutputModuleBuilder = new Dictionary<byte, BuildAnalogOutModule>()
        {
            {
                125,
                (modbusServ, moduleInfo) =>
                    new PhoenixAnalogOutputModule_2702497(
                        moduleInfo.DataOutStartAddress,
                        modbusServ)
            },
            {
                91,
                (modbusServ, moduleInfo) =>
                    new PhoenixAnalogOutputModule_2700775(
                        modbusServ,
                        moduleInfo.DataInStartAddress,
                        moduleInfo.DataOutStartAddress)
            }
        };

        public PhoenixIOBusConnectionFactory()
        {

        }

        protected async Task<IEnumerable<IoModulePreliminaryInfo>> GetCurrentModuleConfigurationAsync(
            IModbusMaster modServ,
            CancellationToken ct = default)
        {
            ushort moduleCountRegAddr = 1400;
            ushort moduleCount = 0;

            //need to retry here since right after start up mod count is 0 
            while (moduleCount == 0)
            {
                //get modules at runtime
                moduleCount = (await modServ.ReadInputRegistersAsync(0, moduleCountRegAddr, 1))[0];

                await Task.Delay(500);
            }

            ushort modInfoRegAddr = 1401;

            var modInfoCollection =
                (await modServ.ReadInputRegistersAsync(0, modInfoRegAddr, moduleCount))
                .Select((m, i) =>
                {
                    //TODO: KEY NOT FOUND
                    ushort dataBitsLength = (ushort)DecodeDataBitsLength((byte)(m >> 8));
                    ushort dataWordsLength = (ushort)(dataBitsLength / 16);
                    //round up if module takes up a portion of a register
                    if (dataBitsLength % 16 != 0)
                        dataWordsLength++;

                    byte idCode = (byte)m;

                    ushort modIndex = (ushort)i;

                    bool found = _moduleIdCodeDataTypeMap.TryGetValue(idCode, out var mod);

                    if (!found)
                        throw new KeyNotFoundException($"Failed to find IO module information for IO module with id: {idCode}");

                    return new IoModulePreliminaryInfo(dataBitsLength, dataWordsLength, idCode, modIndex, mod.dataType, mod.iotype);
                })
                .OrderBy(m => m.ModuleIndex);

            return modInfoCollection;
        }

        protected DigitalOutputModulesActionsModel BuildDigitalOutputActionsModel(
            IModbusMaster modbusServ,
            IEnumerable<IoModuleInfo> modules)
        {
            var indexToModuleIdMap = new Dictionary<ushort, ushort>();
            var busIndexToModuleIndexMap = new Dictionary<ushort, ushort>();
            var digitalOutputWriteActions = new Dictionary<ushort, Func<IReadOnlyDictionary<ushort, bool>, CancellationToken, Task>>();
            var digitalOutputReadActions = new Dictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, bool>>>>();
            var moduleInfoBusIndexMap = new Dictionary<ModuleIdIOIndexPair, ushort>();
            ushort busDigitalOutputIndex = 1;

            foreach (var modInfo in modules)
            {
                bool found = _digitalOutputModuleBuilder.TryGetValue(modInfo.IdCode, out var digOutModuleBuild);
                //TODO: throw
                if (found)
                {
                    //throw new NullReferenceException($"{failPrefix} No module plugin available for module with id code: {modInfo.IdCode}");
                    var mod = digOutModuleBuild(modbusServ, modInfo);
                    digitalOutputWriteActions.Add(modInfo.ModuleIndex, (points, c) => mod.WriteManyAsync(points, c));
                    digitalOutputReadActions.Add(modInfo.ModuleIndex, (indexes, c) => mod.ReadManyAsync(indexes, c));

                    var ioPointCount = mod.GetNumberOfIOPoints();

                    for (ushort i = 1; i < ioPointCount + 1; i++)
                    {
                        //local copy for closure
                        ushort localIndex = i;
                        moduleInfoBusIndexMap.Add(new ModuleIdIOIndexPair(modInfo.ModuleIndex, i), busDigitalOutputIndex);
                        //build index mapping to link bus indexes to module index
                        indexToModuleIdMap.Add(busDigitalOutputIndex, modInfo.ModuleIndex);
                        //build index mapping to link bus index to local index(i.e. the module's index)
                        busIndexToModuleIndexMap.Add(busDigitalOutputIndex, localIndex);
                        busDigitalOutputIndex++;
                    }
                }
            }

            return new DigitalOutputModulesActionsModel(indexToModuleIdMap, busIndexToModuleIndexMap, digitalOutputWriteActions, digitalOutputReadActions, moduleInfoBusIndexMap);
        }

        protected DigitalInputModulesActionsModel BuildDigitalInputActionsModel(
            IModbusMaster modbusServ,
            IEnumerable<IoModuleInfo> modules)
        {
            var indexToModuleIdMap = new Dictionary<ushort, ushort>();
            var busIndexToModuleIndexMap = new Dictionary<ushort, ushort>();
            var digitalInputWriteActions = new Dictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, bool>>>>();
            var moduleInfoBusIndexMap = new Dictionary<ModuleIdIOIndexPair, ushort>();
            ushort busDigitalInputIndex = 1;

            foreach (var modInfo in modules)
            {
                bool found = _digitalInputModuleBuilder.TryGetValue(modInfo.IdCode, out var digInputModuleBuild);
                //TODO: throw
                if (found)
                {
                    //    throw new NullReferenceException($"{failPrefix} No module plugin available for module with id code: {modInfo.IdCode}");
                    var mod = digInputModuleBuild(modbusServ, modInfo);
                    digitalInputWriteActions.Add(modInfo.ModuleIndex, (indexes, c) => mod.ReadManyAsync(indexes, c));
                    var ioPointCount = mod.GetNumberOfIOPoints();

                    for (ushort i = 1; i < ioPointCount + 1; i++)
                    {
                        //local copy for closure
                        ushort localIndex = i;
                        moduleInfoBusIndexMap.Add(new ModuleIdIOIndexPair(modInfo.ModuleIndex, i), busDigitalInputIndex);
                        indexToModuleIdMap.Add(busDigitalInputIndex, modInfo.ModuleIndex);
                        busIndexToModuleIndexMap.Add(busDigitalInputIndex, localIndex);
                        busDigitalInputIndex++;
                    }
                }
            }

            return new DigitalInputModulesActionsModel(indexToModuleIdMap, busIndexToModuleIndexMap, digitalInputWriteActions, moduleInfoBusIndexMap);
        }

        protected async Task<AnalogInputModulesActionsModel> BuildAnalogInputActionsModelAsync(
            IModbusMaster modbusServ,
            IEnumerable<IoModuleInfo> modules,
            IReadOnlyDictionary<ushort, AnalogInputConfig> analogConfig,
            CancellationToken ct = default)
        {
            var indexToModuleIdMap = new Dictionary<ushort, ushort>();
            var busIndexToModuleIndexMap = new Dictionary<ushort, ushort>();
            var analogInputReadActions = new Dictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, double>>>>();
            var moduleInfoBusIndexMap = new Dictionary<ModuleIdIOIndexPair, ushort>();
            ushort busAnalogInputIndex = 1;

            foreach (var modInfo in modules)
            {
                bool found = _analogInputModuleBuilder.TryGetValue(modInfo.IdCode, out var analogInputModuleBuild);

                if (!found)
                    throw new Exception($"Failed to bind module with id code: {modInfo.IdCode} to appropriate module driver. Please ensure module is supported");

                var mod = analogInputModuleBuild(modbusServ, modInfo);

                analogInputReadActions.Add(modInfo.ModuleIndex, (indexes, c) => mod.ReadManyAsync(indexes, c));
                var ioPointCount = mod.GetNumberOfIOPoints();
                var analogInputConfigMap = new Dictionary<ushort, AnalogInputConfig>();

                for (ushort i = 1; i < ioPointCount + 1; i++)
                {
                    //local copy for closure
                    ushort localIndex = i;

                    if (analogConfig.TryGetValue(busAnalogInputIndex, out var config))
                        analogInputConfigMap.Add(localIndex, config);

                    moduleInfoBusIndexMap.Add(new ModuleIdIOIndexPair(modInfo.ModuleIndex, i), busAnalogInputIndex);
                    indexToModuleIdMap.Add(busAnalogInputIndex, modInfo.ModuleIndex);
                    busIndexToModuleIndexMap.Add(busAnalogInputIndex, localIndex);
                    busAnalogInputIndex++;
                }

                mod.SetInputConfiguration(analogInputConfigMap);
                //Call initialize on each module
                await mod.InitializeAsync(TimeSpan.FromSeconds(3), ct);
            }

            return new AnalogInputModulesActionsModel(busIndexToModuleIndexMap, indexToModuleIdMap, analogInputReadActions, moduleInfoBusIndexMap);
        }

        protected async Task<AnalogOutputModulesActionsModel> BuildAnalogOutputActionsModelAsync(
            IModbusMaster modbusServ,
            IEnumerable<IoModuleInfo> modules,
            IReadOnlyDictionary<ushort, AnalogMeasurementRange> analogConfig,
            CancellationToken ct = default)
        {
            var indexToModuleIdMap = new Dictionary<ushort, ushort>();
            var busIndexToModuleIndexMap = new Dictionary<ushort, ushort>();
            var analogOutputWriteActions = new Dictionary<ushort, Func<IReadOnlyDictionary<ushort, double>, CancellationToken, Task>>();
            var analogOutputReadActions = new Dictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, double>>>>();
            var moduleInfoBusIndexMap = new Dictionary<ModuleIdIOIndexPair, ushort>();
            ushort busAnalogInputIndex = 1;

            foreach (var modInfo in modules)
            {
                bool found = _analogOutputModuleBuilder.TryGetValue(modInfo.IdCode, out var analogOutputModuleBuild);

                if (!found)
                    throw new Exception($"Failed to bind module with id code: {modInfo.IdCode} to appropriate module driver. Please ensure module is supported");

                var mod = analogOutputModuleBuild(modbusServ, modInfo);

                analogOutputWriteActions.Add(modInfo.ModuleIndex, (state, c) => mod.WriteManyAsync(state, c));
                analogOutputReadActions.Add(modInfo.ModuleIndex, (indexes, c) => mod.ReadManyAsync(indexes, c));
                var ioPointCount = mod.GetNumberOfIOPoints();
                var analogOutputConfigMap = new Dictionary<ushort, AnalogMeasurementRange>();

                for (ushort i = 1; i < ioPointCount + 1; i++)
                {
                    //local copy for closure
                    ushort localIndex = i;

                    if (analogConfig.TryGetValue(busAnalogInputIndex, out var config))
                        analogOutputConfigMap.Add(localIndex, config);

                    moduleInfoBusIndexMap.Add(new ModuleIdIOIndexPair(modInfo.ModuleIndex, i), busAnalogInputIndex);
                    indexToModuleIdMap.Add(busAnalogInputIndex, modInfo.ModuleIndex);
                    busIndexToModuleIndexMap.Add(busAnalogInputIndex, localIndex);
                    busAnalogInputIndex++;
                }

                mod.SetOutputConfiguration(analogOutputConfigMap);
                await mod.InitializeAsync(TimeSpan.FromSeconds(3), ct);
            }

            return new AnalogOutputModulesActionsModel(busIndexToModuleIndexMap, indexToModuleIdMap, analogOutputWriteActions, analogOutputReadActions, moduleInfoBusIndexMap);
        }

        /// <summary>
        /// Adds module in/out address info
        /// </summary>
        private IReadOnlyList<IoModuleInfo> AugmentModuleInfo(
            IEnumerable<IoModulePreliminaryInfo> ioModuleInfo)
        {
            //Reference: data sheet 8501_en_04 section 15.6 "Assignment of process data" page 23
            //Address calculation explanation: Each module has some input, output, or both process
            //words associated with it. Input addresses start at 8000. This is incremented for each
            //input module based on how many words are associated with it. The sequence of processing
            //modules is: inputs first, then sorted by module index which is determined by the module's
            //physical location on the bus

            var output = new List<IoModuleInfo>();

            var inProcDataAddressMap = new Dictionary<ushort, ushort>();

            //THE INITIAL PROCESS WORD ADDRESS
            ushort inDataAddress = 8000;

            //HANDLE ALL INPUTS FIRST, ORDERED BY MODULE INDEX
            var inputProcDataMods =
                ioModuleInfo
                .Where(x => x.ModuleProcessDataType == ModuleProcessDataType.Input || x.ModuleProcessDataType == ModuleProcessDataType.InputOutput)
                .OrderBy(x => x.ModuleIndex);

            foreach (var item in inputProcDataMods)
            {
                inProcDataAddressMap.Add(item.ModuleIndex, inDataAddress);
                inDataAddress += item.WordCount;
            }

            //THE START OF OUTPUT ADDRESSES IS THE END OF INPUT ADDRESSES
            ushort outDataAddress = inDataAddress;

            var outProcDataAddressMap = new Dictionary<ushort, ushort>();

            var outProcDataMods =
                ioModuleInfo
                .Where(x => x.ModuleProcessDataType == ModuleProcessDataType.Output || x.ModuleProcessDataType == ModuleProcessDataType.InputOutput)
                .OrderBy(x => x.ModuleIndex);

            foreach (var item in outProcDataMods)
            {
                outProcDataAddressMap.Add(item.ModuleIndex, outDataAddress);
                outDataAddress += item.WordCount;
            }

            foreach (var mod in ioModuleInfo)
            {
                if (!inProcDataAddressMap.TryGetValue(mod.ModuleIndex, out ushort inAddr))
                    inAddr = 0;
                if (!outProcDataAddressMap.TryGetValue(mod.ModuleIndex, out ushort outAddr))
                    outAddr = 0;

                var ioModInfo =
                    new IoModuleInfo(
                        databitCount: mod.DataBitCount,
                        dataOutStartAddress: outAddr,
                        dataInStartAddress: inAddr,
                        dataWordCount: mod.WordCount,
                        idCode: mod.IdCode,
                        mod.ModuleIndex,
                        mod.ModuleProcessDataType,
                        mod.IoType);

                output.Add(ioModInfo);
            }

            return output;
        }

        /// <summary>
        /// Get active connection to io bus
        /// </summary>
        /// <param name="ipAddress"></param>
        /// <param name="connectionTimeout"></param>
        /// <param name="connectionWatchdogInterval"></param>
        /// <param name="analogInConfig">A map where the key is module index and the value is another map where key is io index 1-based and value is config</param>
        /// <param name="port"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<IDisposableIOBus> ConnectAsync(
            string ipAddress,
            TimeSpan connectionTimeout,
            TimeSpan connectionWatchdogInterval,
            IReadOnlyDictionary<ushort, AnalogInputConfig> analogInConfig,
            IReadOnlyDictionary<ushort, AnalogMeasurementRange> analogOutConfig,
            int port = 502,
            CancellationToken ct = default)
        {
            var factory = new ModbusFactory();
            var adapter = new TcpClientAdapter(new TcpClient(ipAddress, port));
            IModbusMaster master = factory.CreateIpMaster(adapter);

            var modInfoCollection = await GetCurrentModuleConfigurationAsync(master, ct).ConfigureAwait(false);

            var modInfo = AugmentModuleInfo(modInfoCollection);

            var moduleMap =
                modInfo
                .GroupBy(m => m.IoType)
                .ToDictionary(g => g.Key, g => g.ToList());

            var digitalInputModules = moduleMap[IoType.DigitalInput];
            var digitalInputActionsModel = BuildDigitalInputActionsModel(master, digitalInputModules);
            var digitalOutputModules = moduleMap[IoType.DigitalOutput];
            var digitalOutputActionsModel = BuildDigitalOutputActionsModel(master, digitalOutputModules);

            if (!moduleMap.TryGetValue(IoType.AnalogInput, out var analogInputModules))
                analogInputModules = new List<IoModuleInfo>();

            var analogInputActionsModel = await BuildAnalogInputActionsModelAsync(master, analogInputModules, analogInConfig, ct);

            if (!moduleMap.TryGetValue(IoType.AnalogOutput, out var analogOutputModules))
                analogOutputModules = new List<IoModuleInfo>();

            var analogOutputActionsModel = await BuildAnalogOutputActionsModelAsync(master, analogOutputModules, analogOutConfig, ct);

            return new IOBus(master, digitalInputActionsModel, digitalOutputActionsModel, analogInputActionsModel, analogOutputActionsModel);
        }
    }

    public enum ModuleProcessDataType
    {
        Input = 1,
        Output = 2,
        InputOutput = 3
    }

    public enum IoType
    {
        DigitalInput = 0,
        DigitalOutput = 1,
        AnalogInput = 2,
        AnalogOutput = 3
    }

    public class IoModulePreliminaryInfo
    {
        public IoModulePreliminaryInfo(
            ushort databitCount,
            ushort wordCount,
            byte idCode,
            ushort moduleIndex,
            ModuleProcessDataType modDataType,
            IoType ioType)
        {
            WordCount = wordCount;
            IdCode = idCode;
            DataBitCount = databitCount;
            ModuleIndex = moduleIndex;
            ModuleProcessDataType = modDataType;
            IoType = ioType;
        }

        public ushort DataBitCount { get; }
        public ushort WordCount { get; }
        public byte IdCode { get; }
        public ushort ModuleIndex { get; }
        public ModuleProcessDataType ModuleProcessDataType { get; }
        public IoType IoType { get; }
    }

    public class IoModuleInfo : IoModulePreliminaryInfo
    {
        public IoModuleInfo(
            ushort databitCount,
            ushort dataOutStartAddress,
            ushort dataInStartAddress,
            ushort dataWordCount,
            byte idCode,
            ushort moduleIndex,
            ModuleProcessDataType modDataType,
            IoType ioType)
            : base(databitCount, dataWordCount, idCode, moduleIndex, modDataType, ioType)
        {
            DataOutStartAddress = dataOutStartAddress;
            DataInStartAddress = dataInStartAddress;
        }

        public ushort DataOutStartAddress { get; }
        public ushort DataInStartAddress { get; }
    }

    public interface IModulesIoIndexModel
    {
        IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIndexMap { get; }
        IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIdMap { get; }
    }

    public interface IDigitalModulesActionsModel : IModulesIoIndexModel
    {
        IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> ModuleInfoToBusBasedIndexMap { get; }
        IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, bool>>>> ModuleBasedIndexToReadActionMap { get; }
    }

    public interface IAnalogModulesActionsModel : IModulesIoIndexModel
    {
        IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> ModuleInfoToBusBasedIndexMap { get; }
        IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, double>>>> ModuleBasedIndexToReadActionMap { get; }
    }


    /// <summary>
    /// Holds the module index and the IO index within a module
    /// </summary>
    public struct ModuleIdIOIndexPair
    {

        public ModuleIdIOIndexPair(
            ushort moduleIndex,
            ushort moduleIOIndex)
        {
            ModuleIndex = moduleIndex;
            ModuleIOIndex = moduleIOIndex;
        }

        public ushort ModuleIndex { get; }
        public ushort ModuleIOIndex { get; }

    }

    public class DigitalOutputModulesActionsModel : IDigitalModulesActionsModel
    {
        public DigitalOutputModulesActionsModel(
            IReadOnlyDictionary<ushort, ushort> ioIndexToModuleIdMap,
            IReadOnlyDictionary<ushort, ushort> busIndexToModuleIndexMap,
            IReadOnlyDictionary<ushort, Func<IReadOnlyDictionary<ushort, bool>, CancellationToken, Task>> moduleWriteActionMap,
            IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, bool>>>> moduleReadActionMap,
            IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> moduleInfoBusIndexMap)
        {
            BusBasedIndexToModuleIndexMap = busIndexToModuleIndexMap;
            BusBasedIndexToModuleIdMap = ioIndexToModuleIdMap;
            ModuleWriteActionMap = moduleWriteActionMap;
            ModuleBasedIndexToReadActionMap = moduleReadActionMap;
            ModuleInfoToBusBasedIndexMap = moduleInfoBusIndexMap;
        }

        public IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIndexMap { get; }
        public IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIdMap { get; }
        public IReadOnlyDictionary<ushort, Func<IReadOnlyDictionary<ushort, bool>, CancellationToken, Task>> ModuleWriteActionMap { get; }
        public IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, bool>>>> ModuleBasedIndexToReadActionMap { get; }
        public IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> ModuleInfoToBusBasedIndexMap { get; }
    }

    public class DigitalInputModulesActionsModel : IDigitalModulesActionsModel
    {
        public DigitalInputModulesActionsModel(
            IReadOnlyDictionary<ushort, ushort> ioIndexToModuleIdMap,
            IReadOnlyDictionary<ushort, ushort> busIndexToModuleIndexMap,
            IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, bool>>>> moduleActionMap,
            IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> moduleInfoBusIndexMap)
        {
            BusBasedIndexToModuleIndexMap = busIndexToModuleIndexMap;
            BusBasedIndexToModuleIdMap = ioIndexToModuleIdMap;
            ModuleBasedIndexToReadActionMap = moduleActionMap;
            ModuleInfoToBusBasedIndexMap = moduleInfoBusIndexMap;
        }
        public IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIndexMap { get; }
        public IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIdMap { get; }
        public IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, bool>>>> ModuleBasedIndexToReadActionMap { get; }
        public IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> ModuleInfoToBusBasedIndexMap { get; }
    }

    public class AnalogInputModulesActionsModel : IAnalogModulesActionsModel
    {
        public AnalogInputModulesActionsModel(
            IReadOnlyDictionary<ushort, ushort> busBasedIndexToModuleIndexMap,
            IReadOnlyDictionary<ushort, ushort> busBasedIndexToModuleIdMap,
            IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, double>>>> moduleBasedIndexToReadActionMap,
            IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> moduleInfoToBusBasedIndexMap)
        {
            BusBasedIndexToModuleIndexMap = busBasedIndexToModuleIndexMap;
            BusBasedIndexToModuleIdMap = busBasedIndexToModuleIdMap;
            ModuleInfoToBusBasedIndexMap = moduleInfoToBusBasedIndexMap;
            ModuleBasedIndexToReadActionMap = moduleBasedIndexToReadActionMap;
        }

        public IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIndexMap { get; }
        public IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIdMap { get; }
        public IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> ModuleInfoToBusBasedIndexMap { get; }
        public IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, double>>>> ModuleBasedIndexToReadActionMap { get; }
    }

    public class AnalogOutputModulesActionsModel : IAnalogModulesActionsModel
    {
        public AnalogOutputModulesActionsModel(
            IReadOnlyDictionary<ushort, ushort> busBasedIndexToModuleIndexMap,
            IReadOnlyDictionary<ushort, ushort> busBasedIndexToModuleIdMap,
            IReadOnlyDictionary<ushort, Func<IReadOnlyDictionary<ushort, double>, CancellationToken, Task>> moduleBasedIndexToWriteActionMap,
            IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, double>>>> moduleBasedIndexToReadActionMap,
            IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> moduleInfoToBusBasedIndexMap)
        {
            BusBasedIndexToModuleIndexMap = busBasedIndexToModuleIndexMap;
            BusBasedIndexToModuleIdMap = busBasedIndexToModuleIdMap;
            ModuleInfoToBusBasedIndexMap = moduleInfoToBusBasedIndexMap;
            ModuleBasedIndexToWriteActionMap = moduleBasedIndexToWriteActionMap;
            ModuleBasedIndexToReadActionMap = moduleBasedIndexToReadActionMap;
        }

        public IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIndexMap { get; }
        public IReadOnlyDictionary<ushort, ushort> BusBasedIndexToModuleIdMap { get; }
        public IReadOnlyDictionary<ModuleIdIOIndexPair, ushort> ModuleInfoToBusBasedIndexMap { get; }
        public IReadOnlyDictionary<ushort, Func<IReadOnlyDictionary<ushort, double>, CancellationToken, Task>> ModuleBasedIndexToWriteActionMap { get; }
        public IReadOnlyDictionary<ushort, Func<IEnumerable<ushort>, CancellationToken, Task<IReadOnlyDictionary<ushort, double>>>> ModuleBasedIndexToReadActionMap { get; }
    }

}
