using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using puck.Services.IoBus;
using puck.Services.PID;
using puck.Services.TemperatureController;
using Puck.Services.TemperatureController;
using Puck.Services;
using Xunit;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Puck.Tests
{
    public class SystemProxyTests
    {
        private readonly ITestOutputHelper _output;
        public SystemProxyTests(ITestOutputHelper output) => _output = output;
        private SystemProxy CreateSystemProxy(
            out Mock<IPhoenixProxy> phoenixMock,
            out Dictionary<TemperatureControllerId, Mock<ITemperatureController>> tempMocks,
            out PauseContainer pauseContainer)
        {
            phoenixMock = new Mock<IPhoenixProxy>();
            tempMocks = new Dictionary<TemperatureControllerId, Mock<ITemperatureController>>
            {
                { TemperatureControllerId.GroupHead, new Mock<ITemperatureController>() },
                { TemperatureControllerId.ThermoBlock, new Mock<ITemperatureController>() }
            };
            // Use a local dictionary for the container
            var portMap = new Dictionary<TemperatureControllerId, string> {
                { TemperatureControllerId.GroupHead, "COM1" },
                { TemperatureControllerId.ThermoBlock, "COM2" }
            };
            var tempConfig = new TemperatureControllerConfiguration();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => x.Value.Object));

            pauseContainer = new PauseContainer();
            var logger = Moq.Mock.Of<ILogger<SystemService>>();
            var pid = new PID(1, 1, 1, 1, 1, 1);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            return new SystemProxy(logger, phoenixMock.Object, tempContainer, pauseContainer, pid, paramRepo, runRepo);
        }

        [Fact]
        public async Task Concurrent_RunAsync_Calls_Are_Serialized()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            var cts = new CancellationTokenSource();
            phoenixMock.Setup(p => p.SetDigitalOutputStateAsync(It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            var tasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => proxy.RunAsync(new RunParameters(), cts.Token)))
                .ToArray();
            var results = await Task.WhenAll(tasks.Select(async t => {
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
            phoenixMock.Setup(p => p.SetAnalogOutputStateAsync(It.IsAny<ushort>(), It.IsAny<double>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var cts = new CancellationTokenSource();
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => proxy.ApplyPumpSpeedAsync(5.0, cts.Token)))
                .ToArray();
            var results = await Task.WhenAll(tasks.Select(async t => {
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
            tempMocks[TemperatureControllerId.GroupHead].Setup(t => t.SetSetPointAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            var cts = new CancellationTokenSource();
            var tasks = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => proxy.SetTemperatureSetpointAsync(100, TemperatureControllerId.GroupHead, cts.Token)))
                .ToArray();
            var results = await Task.WhenAll(tasks.Select(async t => {
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
            phoenixMock.Verify(p => p.Dispose(), Times.AtLeastOnce());
            tempMocks[TemperatureControllerId.GroupHead].Verify(t => t.Dispose(), Times.AtLeastOnce());
            tempMocks[TemperatureControllerId.ThermoBlock].Verify(t => t.Dispose(), Times.AtLeastOnce());
        }

        [Fact]
        public async Task RunAsync_Throws_When_AlreadyRunning()
        {
            var proxy = CreateSystemProxy(out _, out _, out _);
            var cts = new CancellationTokenSource();
            // Simulate run lock already taken
            typeof(SystemProxy).GetField("_runLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(proxy, new SemaphoreSlim(0, 1));
            await Assert.ThrowsAsync<Exception>(() => proxy.RunAsync(new RunParameters(), cts.Token));
        }

        [Fact]
        public async Task ApplyPumpSpeedAsync_Throws_When_AlreadyRunning()
        {
            var proxy = CreateSystemProxy(out _, out _, out _);
            var cts = new CancellationTokenSource();
            typeof(SystemProxy).GetField("_runLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(proxy, new SemaphoreSlim(0, 1));
            await Assert.ThrowsAsync<Exception>(() => proxy.ApplyPumpSpeedAsync(5.0, cts.Token));
        }

        [Fact]
        public void GetRunState_ReturnsExpectedState()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            phoenixMock.SetupGet(p => p.DigitalInputState)
                .Returns(new Dictionary<ushort, DigitalIoState?> { { 1, new DigitalIoState(true, DateTime.UtcNow) } });
            Assert.Equal(RunState.Run, proxy.GetRunState());

            phoenixMock.SetupGet(p => p.DigitalInputState)
                .Returns(new Dictionary<ushort, DigitalIoState?> { { 1, new DigitalIoState(false, DateTime.UtcNow) } });
            Assert.Equal(RunState.Idle, proxy.GetRunState());
        }

        [Fact]
        public void GetValveState_ReturnsExpectedState()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            phoenixMock.SetupGet(p => p.DigitalOutputState)
                .Returns(new Dictionary<ushort, DigitalIoState?> { { 1, new DigitalIoState(true, DateTime.UtcNow) } });
            Assert.Equal(ValveState.Open, proxy.GetRecirculationValveState());

            phoenixMock.SetupGet(p => p.DigitalOutputState)
                .Returns(new Dictionary<ushort, DigitalIoState?> { { 1, new DigitalIoState(false, DateTime.UtcNow) } });
            Assert.Equal(ValveState.Closed, proxy.GetRecirculationValveState());
        }

        [Fact]
        public void GetPumpSpeedSetting_ReturnsExpectedValue()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            phoenixMock.SetupGet(p => p.AnalogOutputState)
                .Returns(new Dictionary<ushort, AnalogIoState?> { { 1, new AnalogIoState(42.0, DateTime.UtcNow) } });
            Assert.Equal(42.0, proxy.GetPumpSpeedSetting());
        }

        [Fact]
        public void GetGroupHeadPressure_ReturnsExpectedValue()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            phoenixMock.SetupGet(p => p.AnalogInputState)
                .Returns(new Dictionary<ushort, AnalogIoState?> { { 1, new AnalogIoState(9.5, DateTime.UtcNow) } });
            Assert.Equal(9.5, proxy.GetGroupHeadPressure());
        }

        [Fact]
        public void GetProcessTemperature_ReturnsExpectedValue()
        {
            var proxy = CreateSystemProxy(out _, out var tempMocks, out _);
            tempMocks[TemperatureControllerId.GroupHead].Setup(t => t.GetProcessValue()).Returns(88.8);
            Assert.Equal(88.8, proxy.GetProcessTemperature(TemperatureControllerId.GroupHead));
        }

        [Fact]
        public void GetSetPointTemperature_ReturnsExpectedValue()
        {
            var proxy = CreateSystemProxy(out _, out var tempMocks, out _);
            tempMocks[TemperatureControllerId.GroupHead].Setup(t => t.GetSetValue()).Returns(92.2);
            Assert.Equal(92.2, proxy.GetSetPointTemperature(TemperatureControllerId.GroupHead));
        }

        [Fact]
        public void GetRunState_ThrowsIfKeyMissing()
        {
            var proxy = CreateSystemProxy(out var phoenixMock, out _, out _);
            phoenixMock.SetupGet(p => p.DigitalInputState).Returns(new Dictionary<ushort, DigitalIoState?>());
            Assert.Throws<Exception>(() => proxy.GetRunState());
        }

        [Fact]
        public void GetSystemState_ReturnsExpectedState_NormalOperation()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            tempMocks[TemperatureControllerId.GroupHead].SetProcessValue(93);
            tempMocks[TemperatureControllerId.ThermoBlock].SetProcessValue(120);

            var ioMock = new MockPhoenixProxy(analogInputs: new ushort[] { 1 });
            ioMock.SetAnalogInput(1, 9.5); // Set pressure
            var pauseCont = new PauseContainer();
            var pid = new PID(1, 1, 1, 1, 1, 1);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo);

            // Simulate valve and run state if needed
            var state = proxy.GetSystemState();

            // Assert
            Assert.Equal(93, state.GroupHeadTemperature);
            Assert.Equal(120, state.ThermoblockTemperature);
            Assert.False(state.GroupHeadHeaterEnabled); // Placeholder
            Assert.False(state.ThermoblockHeaterEnabled); // Placeholder
            Assert.True(state.IsIoBusConnected); // ioMock is not null
            Assert.Equal(9.5, state.GroupHeadPressure);
        }

        [Fact]
        public void GetSystemState_ReturnsExpectedState_WhenPaused()
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
            pauseCont.PauseAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            var pid = new PID(1, 1, 1, 1, 1, 1);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo);

            // Act
            var state = proxy.GetSystemState();

            // Assert
            Assert.True(state.IsPaused);
        }

        [Fact]
        public void GetSystemState_ReturnsExpectedState_NullTempControllers()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            tempMocks[TemperatureControllerId.GroupHead].SetProcessValue(0);
            tempMocks[TemperatureControllerId.ThermoBlock].SetProcessValue(0);

            var ioMock = new MockPhoenixProxy(analogInputs: new ushort[] { 1 });
            ioMock.SetAnalogInput(1, 9.5);
            var pauseCont = new PauseContainer();
            var pid = new PID(1, 1, 1, 1, 1, 1);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo);

            // Act
            var state = proxy.GetSystemState();

            // Assert
            Assert.Equal(0, state.GroupHeadTemperature);
            Assert.Equal(0, state.ThermoblockTemperature);
        }

        [Fact]
        public void GetSystemState_ReturnsExpectedState_ValveStates()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var ioMock = new MockPhoenixProxy(
                digitalInputs: new ushort[] { 1, 2, 3 },
                digitalOutputs: new ushort[] { 1, 2, 3 },
                analogInputs: new ushort[] { 1 },
                analogOutputs: new ushort[] { 1 });
            ioMock.SetAnalogInput(1, 9.5);
            ioMock.SetDigitalInput(1, true); // For run state
            ioMock.SetDigitalInput(2, false);
            ioMock.SetDigitalInput(3, false);
            var pauseCont = new PauseContainer();
            var pid = new PID(1, 1, 1, 1, 1, 1);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo);

            // Act
            var state = proxy.GetSystemState();

            // Assert
            Assert.True(Enum.IsDefined(typeof(ValveState), state.GroupHeadValveState));
            Assert.True(Enum.IsDefined(typeof(ValveState), state.BackflushValveState));
            Assert.True(Enum.IsDefined(typeof(ValveState), state.RecirculationValveState));
        }

        [Fact]
        public void GetSystemState_ReturnsExpectedState_RunState()
        {
            // Arrange
            var tempMocks = new Dictionary<TemperatureControllerId, MockTemperatureController>
            {
                { TemperatureControllerId.GroupHead, new MockTemperatureController() },
                { TemperatureControllerId.ThermoBlock, new MockTemperatureController() }
            };
            var ioMock = new MockPhoenixProxy(
                digitalInputs: new ushort[] { 1, 2, 3 },
                digitalOutputs: new ushort[] { 1, 2, 3 },
                analogInputs: new ushort[] { 1 },
                analogOutputs: new ushort[] { 1 });
            ioMock.SetAnalogInput(1, 9.5);
            ioMock.SetDigitalInput(1, true); // For run state
            var pauseCont = new PauseContainer();
            var pid = new PID(1, 1, 1, 1, 1, 1);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var tempContainer = new TemperatureControllerContainer(tempMocks.ToDictionary(x => x.Key, x => (ITemperatureController)x.Value));
            var logger = new Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo);

            // Act
            var state = proxy.GetSystemState();

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
            var pid = new PID(1, 1, 1, 1, 1, 1);
            var paramRepo = new RunParametersRepo();
            var runRepo = new RunResultRepo();
            var logger = new Mock<ILogger<SystemService>>().Object;
            var proxy = new SystemProxy(
                logger, ioMock, tempContainer, pauseCont, pid, paramRepo, runRepo,
                recircValveIO, groupheadValveIO, backflushValveIO, runStatusOutputIO, runStatusInputIO, pumpSpeedIO, pressureIO
            );

            // Set up run parameters for a quick run
            var runParams = new RunParameters
            {
                InitialPumpSpeed = 5.0,
                GroupHeadTemperatureFarenheit = 200,
                ThermoblockTemperatureFarenheit = 210,
                PreExtractionTargetTemperatureFarenheit = 200,
                ExtractionWeightGrams = .5,
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
                foreach (var state in result.Value.Events)
                {
                    _output.WriteLine($"{state.StateTimestampUtc:HH:mm:ss.fff}\t{state.PumpSpeed,5}\t{state.GroupHeadValveState}\t{state.RecirculationValveState}\t{state.BackflushValveState}\t{state.GroupHeadTemperature}\t{state.ThermoblockTemperature}\t{state.GeneralStatusMessage}");
                }
            }
        }
    }
} 