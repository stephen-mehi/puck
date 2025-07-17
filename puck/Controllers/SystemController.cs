using System.ComponentModel.DataAnnotations;
using System.IO.Ports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Puck.Services;

namespace Puck.Controllers;

[ApiController]
[Route("[controller]")]
public class SystemController : ControllerBase
{
    private readonly ILogger<SystemController> _logger;
    private readonly SystemProxy _proxy;
    private readonly PauseContainer _pauseCont;
    private readonly RunParametersRepo _runParamsRepo;
    private readonly RunResultRepo _runResultRepo;

    public SystemController(
        ILogger<SystemController> logger,
        SystemProxy proxy,
        PauseContainer pauseCont,
        RunParametersRepo runParamsRepo,
        RunResultRepo runResultRepo)
    {
        _runResultRepo = runResultRepo;
        _logger = logger;
        _proxy = proxy;
        _pauseCont = pauseCont;
        _runParamsRepo = runParamsRepo;
    }

    [HttpGet]
    [Route("state")]
    public async Task<IActionResult> GetIoStateAsync(CancellationToken ct = default)
    {
        var state = new
        {
            ThermoBlockTemperature = await _proxy.GetProcessTemperatureAsync(TemperatureControllerId.ThermoBlock, ct),
            GroupHeadTemperature = await _proxy.GetProcessTemperatureAsync(TemperatureControllerId.GroupHead, ct),
            ThermoBlockTemperatureSetPoint = await _proxy.GetSetPointTemperatureAsync(TemperatureControllerId.ThermoBlock, ct),
            GroupHeadTemperatureSetPoint = await _proxy.GetSetPointTemperatureAsync(TemperatureControllerId.GroupHead, ct),
            Pressure = _proxy.GetGroupHeadPressure(),
            PumpSpeed = _proxy.GetPumpSpeedSetting(),
            RunState = _proxy.GetRunState().ToString(),
            RecirculationValveState = _proxy.GetRecirculationValveState().ToString(),
            GroupHeadValveState = _proxy.GetGroupHeadValveState().ToString(),
            BackFlushValveState = _proxy.GetBackFlushValveState().ToString()
        };

        return Ok(state);
    }

    [HttpPost]
    [Route("run-status/idle")]
    public async Task<IActionResult> PostRunStatusIdle(CancellationToken ct = default)
    {
        _logger.LogInformation("Posted idle");
        await _proxy.SetRunStateIdleAsync(ct);
        return Ok("Set to idle");
    }

    [HttpPost]
    [Route("pump/run")]
    public async Task<IActionResult> PostRunPump(CancellationToken ct = default)
    {
        _logger.LogInformation("Posted run pump");
        await _proxy.ApplyPumpSpeedAsync(5, ct);
        return Ok("pump running");
    }

    [HttpPost]
    [Route("pump/stop")]
    public async Task<IActionResult> PostStopPump(CancellationToken ct = default)
    {
        _logger.LogInformation("Posted stop pump");
        await _proxy.StopPumpAsync(ct);
        return Ok("pump stopped");
    }

    [HttpGet]
    [Route("run/result/current")]
    public IActionResult GetCurrentRunResult()
    {
        var res = _runResultRepo.GetLatestRunResultOrDefault();

        if (res == null)
            return BadRequest();

        //TODO: VERIFY THIS IS CORRECT 
        return Ok(JsonContent.Create<RunResult>(res.Value));
    }


    [HttpPost]
    [Route("run-status/run")]
    public async Task<IActionResult> PostRunStatusRun(
        [FromBody] RunParameters runParameters,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Posted run");

        ValidateRunParameters(runParameters);
        await _proxy.RunAsync(runParameters, ct);
        return Ok("Set to run");
    }

    [HttpPost]
    [Route("run-status/run/{runParamsId}")]
    public async Task<IActionResult> PostRunStatusRun(
        [FromQuery] string runParamsId,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Posted run");

        if (string.IsNullOrEmpty(runParamsId))
            throw new Exception("Run parameters id must be included in query string. Otherwise use endpoint that uses default run params");

        var runParams = _runParamsRepo.GetRunParametersById(runParamsId);

        await _proxy.RunAsync(runParams, ct);
        return Ok("Set to run");
    }

    [HttpPost]
    [Route("run-status/run/default")]
    public async Task<IActionResult> PostRunStatusRunDefault(
        CancellationToken ct = default)
    {
        _logger.LogInformation("Posted run");

        var runParams = _runParamsRepo.GetActiveParameters();

        await _proxy.RunAsync(runParams, ct);

        return Ok("Set to run");
    }

    [HttpPost]
    [Route("valves/recirc/state/open")]
    public async Task<IActionResult> PostRecircValveOpen(CancellationToken ct = default)
    {
        _logger.LogInformation("Set recirc valve open");
        await _proxy.SetRecirculationValveStateOpenAsync(ct);
        return Ok("Set recirc valve open");
    }

    [HttpPost]
    [Route("valves/recirc/state/closed")]
    public async Task<IActionResult> PostRecircValveClosed(CancellationToken ct = default)
    {
        _logger.LogInformation("Set recirc valve closed");
        await _proxy.SetRecirculationValveStateClosedAsync(ct);
        return Ok("Set recirc valve closed");
    }

    [HttpPost]
    [Route("pause")]
    public async Task<IActionResult> PostPause(CancellationToken ct = default)
    {
        _logger.LogInformation("Pause requested");
        await _pauseCont.PauseAsync(ct);
        return Ok("paused");
    }

    [HttpPost]
    [Route("resume")]
    public async Task<IActionResult> PostResume(CancellationToken ct = default)
    {
        _logger.LogInformation("Resume requested");
        await _pauseCont.ResumeAsync(ct);
        return Ok("resumed");
    }


    private void ValidateRunParameters(
        RunParameters runParams)
    {
        if (runParams == null)
            throw new Exception();

        //TODO: ADD ALL VALIDATION
    }
}
