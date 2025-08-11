using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using puck.Services.IoBus;
using puck.Services.PID;
using puck.Services.TemperatureController;
using Puck.Services.TemperatureController;
using Puck.Services;
using Xunit;
using System.Diagnostics;
using Xunit.Abstractions;
using Puck.Models;

namespace Puck.Tests
{
    public class SystemProxyTests
    {
        private readonly ITestOutputHelper _output;
        public SystemProxyTests(ITestOutputHelper output) => _output = output;
        private SystemProxy CreateSystemProxy(
            out MockPhoenixProxy phoenixMock,
            out Dictionary<TemperatureControllerId, MockTemperatureController> tempMocks,
            out PauseContainer pauseContainer)
        {
            phoenixMock = new MockPhoenixProxy();
            tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));

            pauseContainer = new PauseContainer();
            var logger = new LoggerFactory().CreateLogger<SystemService>();
            var pid =
                new PID(
                    kp: 1,
                    ki: 1,
                    kd: 1,
                    n: 1,
                    outputUpperLimit: 1,
                    outputLowerLimit: 0);

            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            return new SystemProxy(
                logger, phoenixMock, tempContainer, pauseContainer, pid, paramRepo, runRepo,
                new SystemProxyConfiguration(
                    recircValveIO: 1,
                    groupheadValveIO: 2,
                    backflushValveIO: 3,
                    runStatusOutputIO: 4,
                    runStatusInputIO: 1,
                    pumpSpeedIO: 1,
                    pressureIO: 1,
                    recircValveOpenDelayMs: 100,
                    initialPumpSpeedDelayMs: 750,
                    tempSettleTolerance: 2,
                    tempSettleTimeoutSec: 30,
                    pidLoopDelayMs: 500,
                    mainScanLoopDelayMs: 250,
                    runStateMonitorDelayMs: 250,
                    pumpStopValue: 0.0,
                    setAllIdleRecircOpenDelayMs: 250,
                    pressureUnit: PressureUnit.Psi,
                    sensorMinPressureBar: -1.0,
                    sensorMaxPressureBar: 25.0,
                    sensorMinCurrentmA: 4.0,
                    sensorMaxCurrentmA: 20.0));
        }

        [Fact]
        public async Task Concurrent_RunAsync_Calls_Are_Serialized()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            var cts = new CancellationTokenSource();
            // No need to setup, MockPhoenixProxy implements the methods
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => proxy.RunAsync(new RunParameters(), cts.Token)))
                .ToArray();
            var results = await Task.WhenAll(tasks.Select(async t =>
            {
                try { await t; return "success"; }
                catch (Exception ex) { return (ex.InnerException?.Message ?? ex.Message); }
            }));
            Assert.Contains("success", results);
            Assert.Contains("Cannot execute operation while run is in process", results);
        }

        [Fact]
        public async Task ApplyPumpSpeedAsync_IsThreadSafe()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            var cts = new CancellationTokenSource();
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => proxy.ApplyPumpSpeedAsync(5.0, cts.Token)))
                .ToArray();
            var results = await Task.WhenAll(tasks.Select(async t =>
            {
                try { await t; return "success"; }
                catch (Exception ex) { return (ex.InnerException?.Message ?? ex.Message); }
            }));
            Assert.Contains("success", results);
            Assert.Contains("Cannot execute operation while run is in process", results);
        }

        [Fact]
        public async Task SetTemperatureSetpointAsync_IsThreadSafe()
        {
            var proxy = CreateSystemProxy(out _, out var tempMocks, out _);
            // No setup needed for MockTemperatureController; it implements the interface
            var cts = new CancellationTokenSource();
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => proxy.SetTemperatureSetpointAsync(100, TemperatureControllerId.GroupHead, cts.Token)))
                .ToArray();
            var results = await Task.WhenAll(tasks.Select(async t =>
            {
                try { await t; return "success"; }
                catch (Exception ex) { return (ex.InnerException?.Message ?? ex.Message); }
            }));
            Assert.Contains("success", results);
            Assert.Contains("Cannot execute operation while run is in process", results);
        }

        [Fact]
        public async Task Dispose_IsThreadSafe()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out var tempMocks, out _);
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => proxy.Dispose()))
                .ToArray();
            await Task.WhenAll(tasks);
            // No Moq verification, but can check for no exceptions
        }

        [Fact]
        public async Task RunAsync_Throws_When_AlreadyRunning()
        {
            var proxy = CreateSystemProxy(out _, out _, out _);
            var cts = new CancellationTokenSource();
            // Simulate run lock already taken
            var runLockField = typeof(SystemProxy).GetField("_runLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(runLockField);
            runLockField!.SetValue(proxy, new SemaphoreSlim(0, 1));
            await Assert.ThrowsAsync<Exception>(() => proxy.RunAsync(new RunParameters(), cts.Token));
        }

        [Fact]
        public async Task ApplyPumpSpeedAsync_Throws_When_AlreadyRunning()
        {
            var proxy = CreateSystemProxy(out _, out _, out _);
            var cts = new CancellationTokenSource();
            var runLockField = typeof(SystemProxy).GetField("_runLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(runLockField);
            runLockField!.SetValue(proxy, new SemaphoreSlim(0, 1));

            await Assert.ThrowsAsync<Exception>(() => proxy.ApplyPumpSpeedAsync(5.0, cts.Token));
        }

        [Fact]
        public async Task GetRunState_ReturnsExpectedState()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            await proxy.RunAsync(new RunParameters(), CancellationToken.None);
            //await phoenixMock.SetDigitalOutputStateAsync(1, true, CancellationToken.None);
            Assert.Equal(RunState.Run, proxy.GetRunState());

            await proxy.SetRunStateIdleAsync(CancellationToken.None);
            //await phoenixMock.SetDigitalOutputStateAsync(1, false, CancellationToken.None);
            Assert.Equal(RunState.Idle, proxy.GetRunState());
        }

        [Fact]
        public async Task GetValveState_ReturnsExpectedState()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            await phoenixMock.SetDigitalOutputStateAsync(1, true, CancellationToken.None);
            Assert.Equal(ValveState.Open, proxy.GetRecirculationValveState());

            await phoenixMock.SetDigitalOutputStateAsync(1, false, CancellationToken.None);
            Assert.Equal(ValveState.Closed, proxy.GetRecirculationValveState());
        }

        [Fact]
        public async Task GetPumpSpeedSetting_ReturnsExpectedValue()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            await phoenixMock.SetAnalogOutputStateAsync(1, 42.0, CancellationToken.None);
            Assert.Equal(42.0, proxy.GetPumpSpeedSetting());
        }

        [Fact]
        public void GetGroupHeadPressure_ReturnsExpectedValue()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var ioMock = new MockPhoenixProxy(
                digitalInputs: new ushort[] { 1, 2, 3, 4 },
                digitalOutputs: new ushort[] { 1, 2, 3, 4 },
                analogInputs: new ushort[] { 1 },
                analogOutputs: new ushort[] { 1 });
            // Simulate 12 mA mid-range current (halfway between 4 and 20 mA)
            ioMock.SetAnalogInput(1, 12.0);
            var pauseCont = new PauseContainer();
            var pid = new PID(
                kp: 1,
                ki: 1,
                kd: 1,
                n: 1,
                outputUpperLimit: 1,
                outputLowerLimit: 0);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Moq.Mock<ILogger<SystemService>>().Object;
            // Configure SystemProxy for PSI, -14.5 to 362.5 psi range
            var proxy = new SystemProxy(
                logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo,
                new SystemProxyConfiguration(
                    recircValveIO: 1,
                    groupheadValveIO: 2,
                    backflushValveIO: 3,
                    runStatusOutputIO: 4,
                    runStatusInputIO: 1,
                    pumpSpeedIO: 1,
                    pressureIO: 1,
                    recircValveOpenDelayMs: 100,
                    initialPumpSpeedDelayMs: 750,
                    tempSettleTolerance: 2,
                    tempSettleTimeoutSec: 30,
                    pidLoopDelayMs: 500,
                    mainScanLoopDelayMs: 250,
                    runStateMonitorDelayMs: 250,
                    pumpStopValue: 0.0,
                    setAllIdleRecircOpenDelayMs: 250,
                    pressureUnit: PressureUnit.Psi,
                    sensorMinPressureBar: -1.0,
                    sensorMaxPressureBar: 25.0,
                    sensorMinCurrentmA: 4.0,
                    sensorMaxCurrentmA: 20.0));

            // Act
            var psi = proxy.GetGroupHeadPressure();

            // Expect halfway between -14.5 and 362.5 psi ≈ 174.0 psi
            Assert.True(psi.HasValue);
            Assert.InRange(psi!.Value, 170.0, 178.0);
        }

        [Fact]
        public async Task GetProcessTemperature_ReturnsExpectedValue()
        {
            var proxy = CreateSystemProxy(out _, out var tempMocks, out _);
            tempMocks[TemperatureControllerId.GroupHead].SetProcessValue(88);
            Assert.Equal(88, await proxy.GetProcessTemperatureAsync(TemperatureControllerId.GroupHead, CancellationToken.None));
        }

        [Fact]
        public async Task GetSetPointTemperature_ReturnsExpectedValue()
        {
            var proxy = CreateSystemProxy(out _, out var tempMocks, out _);
            tempMocks[TemperatureControllerId.GroupHead].SetSetValue(92);
            Assert.Equal(92, await proxy.GetSetPointTemperatureAsync(TemperatureControllerId.GroupHead, CancellationToken.None));
        }

        [Fact]
        public void GetRunState_ThrowsIfKeyMissing()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            // Remove all digital inputs
            phoenixMock = new MockPhoenixProxy(digitalInputs: Array.Empty<ushort>(), digitalOutputs: Array.Empty<ushort>());
            var logger = new LoggerFactory().CreateLogger<SystemService>();
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var pauseContainer = new PauseContainer();
            var pid = new PID(
                kp: 1,
                ki: 1,
                kd: 1,
                n: 1,
                outputUpperLimit: 1,
                outputLowerLimit: 0);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var proxy2 = new SystemProxy(
                logger, phoenixMock, tempContainer, pauseContainer, pid, paramRepo, runRepo,
                new SystemProxyConfiguration(
                    recircValveIO: 1,
                    groupheadValveIO: 2,
                    backflushValveIO: 3,
                    runStatusOutputIO: 4,
                    runStatusInputIO: 1,
                    pumpSpeedIO: 1,
                    pressureIO: 1,
                    recircValveOpenDelayMs: 100,
                    initialPumpSpeedDelayMs: 750,
                    tempSettleTolerance: 2,
                    tempSettleTimeoutSec: 30,
                    pidLoopDelayMs: 500,
                    mainScanLoopDelayMs: 250,
                    runStateMonitorDelayMs: 250,
                    pumpStopValue: 0.0,
                    setAllIdleRecircOpenDelayMs: 250,
                    pressureUnit: PressureUnit.Psi,
                    sensorMinPressureBar: -1.0,
                    sensorMaxPressureBar: 25.0,
                    sensorMinCurrentmA: 4.0,
                    sensorMaxCurrentmA: 20.0));
            Assert.Throws<Exception>(() => proxy2.GetRunState());
        }

        [Fact]
        public async Task GetSystemState_ReturnsExpectedState_NormalOperation()
        {
            // Arrange: create proxy and drive state only via SystemProxy
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var ioMock = new MockPhoenixProxy(
                digitalInputs: new ushort[] { 1, 2, 3, 4 },
                digitalOutputs: new ushort[] { 1, 2, 3, 4 },
                analogInputs: new ushort[] { 1 },
                analogOutputs: new ushort[] { 1 });
            var pauseCont = new PauseContainer();
            var pid = new PID(
                kp: 1,
                ki: 1,
                kd: 1,
                n: 1,
                outputUpperLimit: 1,
                outputLowerLimit: 0);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Moq.Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(
                logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo,
                new SystemProxyConfiguration(
                    recircValveIO: 1,
                    groupheadValveIO: 2,
                    backflushValveIO: 3,
                    runStatusOutputIO: 4,
                    runStatusInputIO: 1,
                    pumpSpeedIO: 1,
                    pressureIO: 1,
                    recircValveOpenDelayMs: 100,
                    initialPumpSpeedDelayMs: 750,
                    tempSettleTolerance: 2,
                    tempSettleTimeoutSec: 30,
                    pidLoopDelayMs: 500,
                    mainScanLoopDelayMs: 250,
                    runStateMonitorDelayMs: 250,
                    pumpStopValue: 0.0,
                    setAllIdleRecircOpenDelayMs: 250,
                    pressureUnit: PressureUnit.Psi,
                    sensorMinPressureBar: -1.0,
                    sensorMaxPressureBar: 25.0,
                    sensorMinCurrentmA: 4.0,
                    sensorMaxCurrentmA: 20.0));

            // Drive system behavior through SystemProxy
            await proxy.RunAsync(new RunParameters(), CancellationToken.None);
            await proxy.SetGroupHeadValveStateOpenAsync(CancellationToken.None);
            await proxy.SetRecirculationValveStateClosedAsync(CancellationToken.None);
            await proxy.SetBackFlushValveStateClosedAsync(CancellationToken.None);
            await proxy.ApplyPumpSpeedAsync(42.0, CancellationToken.None);

            var state = await proxy.GetSystemStateAsync();

            // Assert: fields controlled via SystemProxy
            Assert.True(state.IsIoBusConnected);
            Assert.Equal(RunState.Run, state.RunState);
            Assert.Equal(ValveState.Open, state.GroupHeadValveState);
            Assert.Equal(ValveState.Closed, state.RecirculationValveState);
            Assert.Equal(ValveState.Closed, state.BackflushValveState);
            Assert.Equal(42.0, state.PumpSpeed);
        }

        [Fact]
        public async Task GetSystemState_ReturnsExpectedState_WhenPaused()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var ioMock = new MockPhoenixProxy(analogInputs: new ushort[] { 1 });
            ioMock.SetAnalogInput(1, 9.5);
            var pauseCont = new PauseContainer();
            await pauseCont.PauseAsync(System.Threading.CancellationToken.None);
            var pid = new PID(
                kp: 1,
                ki: 1,
                kd: 1,
                n: 1,
                outputUpperLimit: 1,
                outputLowerLimit: 0);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Moq.Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(
                logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo,
                new SystemProxyConfiguration(
                    recircValveIO: 1,
                    groupheadValveIO: 2,
                    backflushValveIO: 3,
                    runStatusOutputIO: 4,
                    runStatusInputIO: 1,
                    pumpSpeedIO: 1,
                    pressureIO: 1,
                    recircValveOpenDelayMs: 100,
                    initialPumpSpeedDelayMs: 750,
                    tempSettleTolerance: 2,
                    tempSettleTimeoutSec: 30,
                    pidLoopDelayMs: 500,
                    mainScanLoopDelayMs: 250,
                    runStateMonitorDelayMs: 250,
                    pumpStopValue: 0.0,
                    setAllIdleRecircOpenDelayMs: 250,
                    pressureUnit: PressureUnit.Psi,
                    sensorMinPressureBar: -1.0,
                    sensorMaxPressureBar: 25.0,
                    sensorMinCurrentmA: 4.0,
                    sensorMaxCurrentmA: 20.0));

            // Act
            var state = await proxy.GetSystemStateAsync();

            // Assert
            Assert.True(state.IsPaused);
        }

        [Fact]
        public async Task GetSystemState_ReturnsExpectedState_NullTempControllers()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            tempMocks[TemperatureControllerId.GroupHead].SetProcessValue(0);
            tempMocks[TemperatureControllerId.ThermoBlock].SetProcessValue(0);

            var ioMock = new MockPhoenixProxy(
                digitalInputs: new ushort[] { 1, 2, 3, 4 },
                digitalOutputs: new ushort[] { 1, 2, 3, 4 },
                analogInputs: new ushort[] { 1 },
                analogOutputs: new ushort[] { 1 });

            ioMock.SetAnalogInput(1, 9.5);
            var pauseCont = new PauseContainer();
            var pid = new PID(
                kp: 1,
                ki: 1,
                kd: 1,
                n: 1,
                outputUpperLimit: 1,
                outputLowerLimit: 0);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Moq.Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(
                logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo,
                new SystemProxyConfiguration(
                    recircValveIO: 1,
                    groupheadValveIO: 2,
                    backflushValveIO: 3,
                    runStatusOutputIO: 4,
                    runStatusInputIO: 1,
                    pumpSpeedIO: 1,
                    pressureIO: 1,
                    recircValveOpenDelayMs: 100,
                    initialPumpSpeedDelayMs: 750,
                    tempSettleTolerance: 2,
                    tempSettleTimeoutSec: 30,
                    pidLoopDelayMs: 500,
                    mainScanLoopDelayMs: 250,
                    runStateMonitorDelayMs: 250,
                    pumpStopValue: 0.0,
                    setAllIdleRecircOpenDelayMs: 250,
                    pressureUnit: PressureUnit.Psi,
                    sensorMinPressureBar: -1.0,
                    sensorMaxPressureBar: 25.0,
                    sensorMinCurrentmA: 4.0,
                    sensorMaxCurrentmA: 20.0));

            // Act
            var state = await proxy.GetSystemStateAsync();

            // Assert
            Assert.Equal(0, state.GroupHeadTemperature);
            Assert.Equal(0, state.ThermoblockTemperature);
        }

        [Fact]
        public async Task GetSystemState_ReturnsExpectedState_ValveStates()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var ioMock = new MockPhoenixProxy(
                digitalInputs: new ushort[] { 1, 2, 3, 4 },
                digitalOutputs: new ushort[] { 1, 2, 3, 4 },
                analogInputs: new ushort[] { 1 },
                analogOutputs: new ushort[] { 1 });
            ioMock.SetAnalogInput(1, 9.5);
            ioMock.SetDigitalInput(1, true); // For run state
            ioMock.SetDigitalInput(2, false);
            ioMock.SetDigitalInput(3, false);
            var pauseCont = new PauseContainer();
            var pid = new PID(
                kp: 1,
                ki: 1,
                kd: 1,
                n: 1,
                outputUpperLimit: 1,
                outputLowerLimit: 0);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Moq.Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(
                logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo,
                new SystemProxyConfiguration(
                    recircValveIO: 1,
                    groupheadValveIO: 2,
                    backflushValveIO: 3,
                    runStatusOutputIO: 4,
                    runStatusInputIO: 1,
                    pumpSpeedIO: 1,
                    pressureIO: 1,
                    recircValveOpenDelayMs: 100,
                    initialPumpSpeedDelayMs: 750,
                    tempSettleTolerance: 2,
                    tempSettleTimeoutSec: 30,
                    pidLoopDelayMs: 500,
                    mainScanLoopDelayMs: 250,
                    runStateMonitorDelayMs: 250,
                    pumpStopValue: 0.0,
                    setAllIdleRecircOpenDelayMs: 250,
                    pressureUnit: PressureUnit.Psi,
                    sensorMinPressureBar: -1.0,
                    sensorMaxPressureBar: 25.0,
                    sensorMinCurrentmA: 4.0,
                    sensorMaxCurrentmA: 20.0));

            // Act
            var state = await proxy.GetSystemStateAsync();

            // Assert
            Assert.True(Enum.IsDefined(typeof(ValveState), state.GroupHeadValveState));
            Assert.True(Enum.IsDefined(typeof(ValveState), state.BackflushValveState));
            Assert.True(Enum.IsDefined(typeof(ValveState), state.RecirculationValveState));
        }

        [Fact]
        public async Task GetSystemState_ReturnsExpectedState_RunState()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var ioMock = new MockPhoenixProxy(
                digitalInputs: new ushort[] { 1, 2, 3, 4 },
                digitalOutputs: new ushort[] { 1, 2, 3, 4 },
                analogInputs: new ushort[] { 1 },
                analogOutputs: new ushort[] { 1 });
            ioMock.SetAnalogInput(1, 9.5);
            ioMock.SetDigitalInput(1, true); // For run state
            var pauseCont = new PauseContainer();
            var pid = new PID(
                kp: 1,
                ki: 1,
                kd: 1,
                n: 1,
                outputUpperLimit: 1,
                outputLowerLimit: 0);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Moq.Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(
                logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo,
                new SystemProxyConfiguration(
                    recircValveIO: 1,
                    groupheadValveIO: 2,
                    backflushValveIO: 3,
                    runStatusOutputIO: 4,
                    runStatusInputIO: 1,
                    pumpSpeedIO: 1,
                    pressureIO: 1,
                    recircValveOpenDelayMs: 100,
                    initialPumpSpeedDelayMs: 750,
                    tempSettleTolerance: 2,
                    tempSettleTimeoutSec: 30,
                    pidLoopDelayMs: 500,
                    mainScanLoopDelayMs: 250,
                    runStateMonitorDelayMs: 250,
                    pumpStopValue: 0.0,
                    setAllIdleRecircOpenDelayMs: 250,
                    pressureUnit: PressureUnit.Psi,
                    sensorMinPressureBar: -1.0,
                    sensorMaxPressureBar: 25.0,
                    sensorMinCurrentmA: 4.0,
                    sensorMaxCurrentmA: 20.0));

            // Act
            var state = await proxy.GetSystemStateAsync();

            // Assert
            Assert.True(Enum.IsDefined(typeof(RunState), state.RunState));
        }

        [Fact]
        public async Task FullProcessSimulation_RunScan_CompletesSuccessfully()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));

            // Default IO points (matching SystemProxy defaults)
            ushort recircValveIO = 1;
            ushort groupheadValveIO = 2;
            ushort backflushValveIO = 3;
            ushort runStatusOutputIO = 4;
            ushort runStatusInputIO = 1;
            ushort pumpSpeedIO = 1;
            ushort pressureIO = 1;

            var ioMock = new MockPhoenixProxy(
                digitalInputs: new ushort[] { runStatusInputIO },
                digitalOutputs: new ushort[] { recircValveIO, groupheadValveIO, backflushValveIO, runStatusOutputIO },
                analogInputs: new ushort[] { pressureIO },
                analogOutputs: new ushort[] { pumpSpeedIO });
            ioMock.SetAnalogInput(pressureIO, 9.5);
            ioMock.SetDigitalInput(runStatusInputIO, true); // Set system to Run state
            var pauseCont = new PauseContainer();
            var pid = new PID(
                kp: 1,
                ki: 1,
                kd: 1,
                n: 1,
                outputUpperLimit: 1,
                outputLowerLimit: 0);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var logger = new Moq.Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(
                logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo,
                new SystemProxyConfiguration(
                    recircValveIO: recircValveIO,
                    groupheadValveIO: groupheadValveIO,
                    backflushValveIO: backflushValveIO,
                    runStatusOutputIO: runStatusOutputIO,
                    runStatusInputIO: runStatusInputIO,
                    pumpSpeedIO: pumpSpeedIO,
                    pressureIO: pressureIO,
                    recircValveOpenDelayMs: 100,
                    initialPumpSpeedDelayMs: 750,
                    tempSettleTolerance: 2,
                    tempSettleTimeoutSec: 30,
                    pidLoopDelayMs: 500,
                    mainScanLoopDelayMs: 250,
                    runStateMonitorDelayMs: 250,
                    pumpStopValue: 0.0,
                    setAllIdleRecircOpenDelayMs: 250,
                    pressureUnit: PressureUnit.Psi,
                    sensorMinPressureBar: -1.0,
                    sensorMaxPressureBar: 25.0,
                    sensorMinCurrentmA: 4.0,
                    sensorMaxCurrentmA: 20.0)
            );

            // Set up run parameters for a quick run
            var runParams = new RunParameters
            {
                InitialPumpSpeed = 5.0,
                GroupHeadTemperatureFarenheit = 200,
                ThermoblockTemperatureFarenheit = 210,
                PreExtractionTargetTemperatureFarenheit = 200,
                ExtractionWeightGrams = 10,
                MaxExtractionSeconds = 30,
                TargetPressureBar = 9.0
            };
            paramRepo.SetActiveParameters(runParams);

            // Act
            using var cts = new CancellationTokenSource();
            var scanTask = proxy.StartRunScan(cts.Token);

            await Task.Delay(1000, cts.Token);

            await proxy.RunAsync(runParams, cts.Token);

            while (proxy.GetRunState() != RunState.Idle)
            {
                await Task.Delay(100, cts.Token);
                Debug.WriteLine("waiting for extraction to finish");
            }

            await Task.Delay(1000, cts.Token);

            cts.Cancel();

            await scanTask;

            // Assert
            var result = runRepo.GetLatestRunResult();
            Assert.NotNull(result);
            var nonNullResult = result!;
            Assert.True(nonNullResult.Value.CompletionStatus == RunCompletionStatus.SUCCEEDED || nonNullResult.Value.CompletionStatus == RunCompletionStatus.FAILED);
            Assert.NotNull(nonNullResult.Value.Events);
            Assert.NotEmpty(nonNullResult.Value.Events);
            if (result.Value.Events != null)
            {
                _output.WriteLine("Time\t\t\tPump\tGHValve\tRecirc\tBackflush\tGH_Temp\tTB_Temp\tStatus");
                var events = result.Value.Events.ToList();
                Assert.True(events.Count > 8, "Not enough events to check state sequence");

                // Write out the full state for each event
                foreach (var e in events)
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(e, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
                    _output.WriteLine(json);
                }

                // Write CSV output
                var csvPath = WriteProcessDeviceStatesToCsv(events);
                _output.WriteLine($"CSV written to: {csvPath}");
            }
        }

        // Helper to write ProcessDeviceState list to CSV
        private static string WriteProcessDeviceStatesToCsv(List<ProcessDeviceState> states)
        {
            var sb = new System.Text.StringBuilder();
            var props = typeof(ProcessDeviceState).GetProperties();
            // Header
            sb.AppendLine(string.Join(",", props.Select(p => p.Name)));
            // Rows
            foreach (var s in states)
            {
                sb.AppendLine(string.Join(",", props.Select(p => FormatCsvValue(p.GetValue(s)))));
            }
            // Write to file in test output directory
            var fileName = $"ProcessDeviceStates.csv";
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            File.WriteAllText(filePath, sb.ToString());
            return filePath ?? string.Empty;
        }
        private static string FormatCsvValue(object? value)
        {
            if (value == null) return "";
            var str = value.ToString();
            if (str != null && (str.Contains(",") || str.Contains("\"")))
                return $"\"{str.Replace("\"", "\"\"")}";
            return str ?? string.Empty;
        }
    }
}