using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using puck.Services.PID;
using Puck.Services;
using System.ComponentModel.DataAnnotations;
using System.IO.Ports;
using static Puck.Controllers.SystemController;

namespace Puck.Controllers;

[ApiController]
[Route("[controller]/autotune")]
public class AutoTuneController : ControllerBase
{
    private readonly SystemProxy _proxy;

    public AutoTuneController(SystemProxy proxy)
    {
        _proxy = proxy;
    }

    [HttpPost("live")]
    public async Task<IActionResult> PostAutoTuneLive([FromBody] AutoTuneLiveRequest req, CancellationToken ct = default)
    {
        var options = new GeneticPidTuner.GeneticTunerOptions
        {
            Generations = req.Generations ?? 5,
            PopulationSize = req.PopulationSize ?? 8,
            KpRange = (req.KpMin ?? 0.1, req.KpMax ?? 2.5),
            KiRange = (req.KiMin ?? 0.1, req.KiMax ?? 2.5),
            KdRange = (req.KdMin ?? 0.0, req.KdMax ?? 0.5),
            EliteFraction = 0.25,
            TournamentSize = 3,
            EvaluateInParallel = false,
            Patience = 5,
            MinImprovement = 1e-3,
            InitialMutationRate = 0.3,
            FinalMutationRate = 0.1,
            InitialMutationStrength = 0.25,
            FinalMutationStrength = 0.1
        };

        var (kp, ki, kd) = await _proxy.AutoTunePidGeneticLiveAsync(
            ct,
            dt: req.Dt ?? 0.1,
            steps: req.Steps ?? 120,
            targetPressureBar: req.TargetPressureBar ?? 9.0,
            maxSafePressureBar: req.MaxSafePressureBar ?? 12.0,
            options: options);

        return Ok(new { kp, ki, kd });
    }
}

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

    public record AutoTuneLiveRequest(
        double? Dt,
        int? Steps,
        double? TargetPressureBar,
        double? MaxSafePressureBar,
        int? Generations,
        int? PopulationSize,
        double? KpMin,
        double? KpMax,
        double? KiMin,
        double? KiMax,
        double? KdMin,
        double? KdMax
    );

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
        if (runParams == null)
            return NotFound($"Run parameters not found for id '{runParamsId}'");

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
        if (runParams == null)
            return BadRequest("No active run parameters are set");

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
