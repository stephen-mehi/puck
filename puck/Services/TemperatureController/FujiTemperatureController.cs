using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NModbus;
using NModbus.Serial;

namespace Puck.Services;

/// <summary>
/// Modbus register addresses for Fuji PXF Temperature Controller
/// </summary>
internal static class FujiPXFRegisters
{
    // Configuration Registers
    public const ushort SensorType = 0x000F;
    public const ushort ControlLoopStatus = 0x0003;

    // Process Value Limit Registers
    public const ushort ProcessValueLowLimit = 0x0011;
    public const ushort ProcessValueHighLimit = 0x0012;

    // Control Registers
    public const ushort SetValue = 0x00F0;
    public const ushort SetValueIndex = 0x00DC;
    public const ushort PIDIndex = 0x00DD;

    // Input Registers
    public const ushort ProcessValue = 0x0000;

    // Control Loop State Values
    public const ushort ControlLoopRun = 0;
    public const ushort ControlLoopStandby = 1;

    // Scale factor for percentage calculations
    public const double ScaleFactor = 10000.0;
}

public class FujiPXFDriverPortConfiguration
{
    public string PortName { get; }
    public int BaudRate { get; }
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Configuring the serial port settings for Fuji PXF. If a baud rate other than the default value of 9600 is used, it might be required to manually adjust the device to match the specific baud rate.
    /// </summary>
    /// <param name="portName"></param>
    /// <param name="timeout">Time out in seconds</param>
    /// <param name="baudRate"></param>
    public FujiPXFDriverPortConfiguration(
        string portName,
        TimeSpan timeout,
        int baudRate = 9600)
    {
        PortName = portName;
        Timeout = timeout;
        BaudRate = baudRate;
    }
}

internal enum SetValueIndex : ushort
{
    ONE = 1,
    TWO = 2,
    THREE = 3,
    FOUR = 4,
    FIVE = 5,
    SIX = 6,
    SEVEN = 7
}

internal enum PIDIndex : ushort
{
    ONE = 1,
    TWO = 2,
    THREE = 3,
    FOUR = 4,
    FIVE = 5,
    SIX = 6,
    SEVEN = 7
}

public class FujiPXFDriverProvider
{
    public async Task<FujiPXFDriver> ConnectAsync(
        FujiPXFDriverPortConfiguration portConfig,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(portConfig);

        if (string.IsNullOrWhiteSpace(portConfig.PortName))
            throw new ArgumentException("Port name cannot be null or empty", nameof(portConfig));

        SerialPort? port = null;
        IModbusMaster? master = null;

        try
        {
            port = new SerialPort(portConfig.PortName, portConfig.BaudRate)
            {
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                ReadTimeout = (int)portConfig.Timeout.TotalMilliseconds,
                WriteTimeout = (int)portConfig.Timeout.TotalMilliseconds
            };

            using var timeoutCts = new CancellationTokenSource(portConfig.Timeout);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await Task.Run(port.Open, combinedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Connection timeout after {portConfig.Timeout.TotalSeconds:F1}s while connecting to " +
                    $"PXF Temperature Controller on port: {portConfig.PortName}");
            }

            if (!port.IsOpen)
            {
                throw new InvalidOperationException(
                    $"Failed to establish connection to PXF Temperature Controller on port: {portConfig.PortName}. " +
                    $"Port did not open successfully.");
            }

            // Create Modbus master
            var factory = new ModbusFactory();
            var adapter = new SerialPortAdapter(port);
            master = factory.CreateRtuMaster(adapter);

            var driver = new FujiPXFDriver(master, portConfig.Timeout);

            // Transfer ownership to driver (so it will dispose the master)
            master = null;
            port = null;

            return driver;
        }
        catch
        {
            // Clean up resources on failure
            master?.Dispose();
            port?.Dispose();
            throw;
        }
    }
}

public class FujiPXFDriver : IDisposable
{
    private readonly IModbusMaster _master;
    private readonly byte _slaveAddress;
    private readonly TimeSpan _timeout;
    private bool _isDisposed;

    internal FujiPXFDriver(IModbusMaster master, TimeSpan timeout)
    {
        _master = master;
        _slaveAddress = 1;
        _timeout = timeout;
    }

    internal Task SetSensorTypeAsync(ThermocoupleSensorType sensorType, CancellationToken ct = default)
    {
        return WriteSingleRegisterAsync(FujiPXFRegisters.SensorType, (ushort)sensorType, ct);
    }

    /// <summary>
    /// Confirm the connection state of the controller
    /// </summary>
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default)
    {
        try
        {
            var readTask = ReadHoldingRegistersAsync(FujiPXFRegisters.ProcessValueLowLimit, 1, ct);

            if (await Task.WhenAny(Task.Delay(_timeout), readTask) != readTask)
                return false;
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Set PV limits in Fahrenheit
    /// </summary>
    /// <param name="low">Low limit in Fahrenheit</param>
    /// <param name="high">High limit in Fahrenheit</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns></returns>
    public async Task SetProcessValueLimitsAsync(ushort low, ushort high, CancellationToken ct = default)
    {
        if (low >= high)
            throw new ArgumentException($"Low limit ({low}°F) must be less than high limit ({high}°F)");

        await WriteSingleRegisterAsync(FujiPXFRegisters.ProcessValueLowLimit, low, ct);
        await WriteSingleRegisterAsync(FujiPXFRegisters.ProcessValueHighLimit, high, ct);
    }

    /// <summary>
    /// Get PV Limits in Fahrenheit
    /// </summary>
    /// <returns>A tuple containing the low and high temperature limits in Fahrenheit</returns>
    public async Task<(int low, int high)> GetProcessValueLimitsAsync(CancellationToken ct = default)
    {
        var limits = await ReadHoldingRegistersAsync(FujiPXFRegisters.ProcessValueLowLimit, 2, ct);
        return (limits[0], limits[1]);
    }

    /// <summary>
    /// Get control loop status
    /// </summary>
    /// <returns></returns>
    public async Task<bool> IsControlLoopActiveAsync(CancellationToken ct = default)
    {
        bool isActive = (await ReadHoldingRegistersAsync(FujiPXFRegisters.ControlLoopStatus, 1, ct))[0] == 1;
        return isActive;
    }

    /// <summary>
    /// Get set value (SV) in Fahrenheit
    /// </summary>
    /// <returns>The current set value in Fahrenheit</returns>
    public async Task<double> GetSetValueAsync(CancellationToken ct = default)
    {
        var sv = (await ReadHoldingRegistersAsync(FujiPXFRegisters.SetValue, 1, ct))[0];

        (int low, int high) = await GetProcessValueLimitsAsync();

        var val = (sv * (high - low) / FujiPXFRegisters.ScaleFactor) + low;

        return val;
    }

    /// <summary>
    /// Get process value (PV) in Fahrenheit
    /// </summary>
    /// <returns>The current process value in Fahrenheit</returns>
    public async Task<double> GetProcessValueAsync(CancellationToken ct = default)
    {
        (int low, int high) = await GetProcessValueLimitsAsync();

        // percentage of full scale, for example, 556 means pv is at 5.56% of the full scale
        var pv = (await ReadInputRegistersAsync(FujiPXFRegisters.ProcessValue, 1, ct))[0];
        return low + (high - low) * (pv / FujiPXFRegisters.ScaleFactor);
    }

    /// <summary>
    /// Set controller to standby
    /// </summary>
    /// <returns></returns>
    public Task DisableControlLoopAsync(CancellationToken ct = default)
    {
        return WriteSingleRegisterAsync(FujiPXFRegisters.ControlLoopStatus, FujiPXFRegisters.ControlLoopStandby, ct);
    }

    /// <summary>
    /// Set controller to RUN
    /// </summary>
    /// <returns></returns>
    public Task EnableControlLoopAsync(CancellationToken ct = default)
    {
        return WriteSingleRegisterAsync(FujiPXFRegisters.ControlLoopStatus, FujiPXFRegisters.ControlLoopRun, ct);
    }

    /// <summary>
    /// Choose which set value number to be used for control
    /// Set value numbers can be 1-7
    /// </summary>
    /// <param name="setValueIndex"></param>
    /// <returns></returns>
    internal Task SelectSetValueIndexAsync(SetValueIndex setValueIndex, CancellationToken ct = default)
    {
        return WriteSingleRegisterAsync(FujiPXFRegisters.SetValueIndex, (ushort)setValueIndex, ct);
    }


    /// <summary>
    /// Choose which set value number to be used for control
    /// Set value numbers can be 1-7
    /// </summary>
    /// <param name="sv"></param>
    /// <returns></returns>
    internal Task SelectPIDIndexAsync(PIDIndex pidIndex, CancellationToken ct = default)
    {
        return WriteSingleRegisterAsync(FujiPXFRegisters.PIDIndex, (ushort)pidIndex, ct);
    }


    /// <summary>
    /// Set the target temperature (SV) in Fahrenheit
    /// </summary>
    /// <param name="setValue">Target temperature in Fahrenheit</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns></returns>
    public async Task SetSetValueAsync(int setValue, CancellationToken ct = default)
    {
        (int low, int high) = await GetProcessValueLimitsAsync();

        if (setValue < low || setValue > high)
            throw new ArgumentOutOfRangeException(
                nameof(setValue),
                setValue,
                $"Set value must be within the configured range ({low}°F, {high}°F). " +
                $"Current process value limits can be adjusted using SetProcessValueLimitsAsync().");

        var value = (ushort)(((double)(setValue - low)) / (double)(high - low) * FujiPXFRegisters.ScaleFactor);

        await WriteSingleRegisterAsync(FujiPXFRegisters.SetValue, value, ct);
    }

    #region helper functions

    /// <summary>
    /// Executes a Modbus operation with timeout and cancellation support (generic)
    /// </summary>
    private async Task<T> ExecuteWithTimeoutAsync<T>(
        Task<T> operation,
        string operationDescription,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await operation.WaitAsync(combinedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Temperature Controller: Timeout occurred during {operationDescription} after {_timeout.TotalMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException($"Temperature Controller: Operation was cancelled during {operationDescription}");
        }
    }

    /// <summary>
    /// Executes a Modbus operation with timeout and cancellation support (non-generic)
    /// </summary>
    private async Task ExecuteWithTimeoutAsync(
        Task operation,
        string operationDescription,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await operation.WaitAsync(combinedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Temperature Controller: Timeout occurred during {operationDescription} after {_timeout.TotalMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException($"Temperature Controller: Operation was cancelled during {operationDescription}");
        }
    }

    private async Task WriteSingleRegisterAsync(ushort registerAddress, ushort value, CancellationToken ct)
    {
        var writeTask = _master.WriteSingleRegisterAsync(_slaveAddress, registerAddress, value);
        await ExecuteWithTimeoutAsync(writeTask, $"writing to register 0x{registerAddress:X4}", ct);
    }

    private async Task<ushort[]> ReadHoldingRegistersAsync(ushort registerAddress, ushort numberOfRegisters, CancellationToken ct)
    {
        var readTask = _master.ReadHoldingRegistersAsync(_slaveAddress, registerAddress, numberOfRegisters);
        return await ExecuteWithTimeoutAsync(readTask, $"reading holding registers 0x{registerAddress:X4}", ct);
    }

    private async Task<ushort[]> ReadInputRegistersAsync(ushort registerAddress, ushort numberOfRegisters, CancellationToken ct)
    {
        var readTask = _master.ReadInputRegistersAsync(_slaveAddress, registerAddress, numberOfRegisters);
        return await ExecuteWithTimeoutAsync(readTask, $"reading input registers 0x{registerAddress:X4}", ct);
    }
    #endregion

    #region IDisposable

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        if (disposing)
        {
            try
            {
                _master.Dispose();
            }
            catch (Exception) { }
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

public enum ThermocoupleSensorType
{
    JPT1 = 0,   // 0 : JPT1 0.0 to 150.0°C
    JPT2 = 1,   // 1 : JPT2 0.0 to 300.0°C
    JPT3 = 2,   // 2 : JPT3 0.0 to 500.0°C
    JPT4 = 3,   // 3 : JPT4 0.0 to 600.0°C
    JPT5 = 4,   // 4 : JPT5 -50.0 to 100.0°C
    JPT6 = 5,   // 5 : JPT6 -100.0 to 200.0°C
    JPT7 = 6,   // 6 : JPT7 -199.9 to 600.0°C
    PT1 = 7,    // 7 : PT1 0.0 to 150.0°C
    PT2 = 8,    // 8 : PT2 0.0 to 300.0°C
    PT3 = 9,    // 9 : PT3 0.0 to 500.0°C
    PT4 = 10,   // 10 : PT4 0.0 to 600.0°C
    PT5 = 11,   // 11 : PT5 -50.0 to 100.0°C
    PT6 = 12,   // 12 : PT6 -100.0 to 200.0°C
    PT7 = 13,   // 13 : PT7 -199.9 to 600.0°C
    PT8 = 14,   // 14 : PT8 -200 to 850°C
    J1 = 15,    // 15 : J1 0.0 to 400.0°C
    J2 = 16,    // 16 : J2 -20.0 to 400.0°C
    J3 = 17,    // 17 : J3 0.0 to 800.0°C
    J4 = 18,    // 18 : J4 -200 to 1300°C
    K1 = 19,    // 19 : K1 0 to 400°C
    K2 = 20,    // 20 : K2 -20.0 to 500.0°C
    K3 = 21,    // 21 : K3 0.0 to 800.0°C
    K4 = 22,    // 22 : K4 -200 to 1300°C
    R = 23,     // 23 : R 0 to 1700°C
    B = 24,     // 24 : B 0 to 1800°C
    S = 25,     // 25 : S 0 to 1700°C
    T1 = 26,    // 26 : T1 -199.9 to 200.0°C
    T2 = 27,    // 27 : T2 -199.9 to 400.0°C
    E1 = 28,    // 28 : E1 0.0 to 800.0°C
    E2 = 29,    // 29 : E2 -150.0 to 800.0°C
    E3 = 30,    // 30 : E3 -200 to 800°C
    L = 31,     // 31 : L -100 to 850°C
    U1 = 32,    // 32 : U1 -199.9 to 400.0°C
    U2 = 33,    // 33 : U2 -200 to 400°C
    N = 34,     // 34 : N -200 to 1300°C
    W = 35,     // 35 : W 0 to 2300°C
    PL2 = 36    // 36 : PL-2 0 to 1300°C
}
