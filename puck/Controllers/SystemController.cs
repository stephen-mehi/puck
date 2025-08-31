using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using puck.Services.PID;
using Puck.Services;
using Puck.Services.Persistence;
using System.ComponentModel.DataAnnotations;
using System.IO.Ports;
using static Puck.Controllers.SystemController;

namespace Puck.Controllers;

//[ApiController]
//[Route("[controller]/autotune")]
//public class AutoTuneController : ControllerBase
//{
//    private readonly SystemProxy _proxy;

//    public AutoTuneController(SystemProxy proxy)
//    {
//        _proxy = proxy;
//    }

//    [HttpPost("live")]
//    public async Task<IActionResult> PostAutoTuneLive([FromBody] AutoTuneLiveRequest req, CancellationToken ct = default)
//    {
//        var options = new GeneticPidTuner.GeneticTunerOptions
//        {
//            Generations = req.Generations ?? 5,
//            PopulationSize = req.PopulationSize ?? 8,
//            KpRange = (req.KpMin ?? 0.1, req.KpMax ?? 2.5),
//            KiRange = (req.KiMin ?? 0.1, req.KiMax ?? 2.5),
//            KdRange = (req.KdMin ?? 0.0, req.KdMax ?? 0.5),
//            EliteFraction = 0.25,
//            TournamentSize = 3,
//            EvaluateInParallel = false,
//            Patience = 5,
//            MinImprovement = 1e-3,
//            InitialMutationRate = 0.3,
//            FinalMutationRate = 0.1,
//            InitialMutationStrength = 0.25,
//            FinalMutationStrength = 0.1
//        };

//        var (kp, ki, kd) = await _proxy.AutoTunePidGeneticLiveAsync(
//            ct,
//            dt: req.Dt ?? 0.1,
//            steps: req.Steps ?? 120,
//            targetPressureBar: req.TargetPressureBar ?? 9.0,
//            maxSafePressureBar: req.MaxSafePressureBar ?? 12.0,
//            options: options);

//        return Ok(new { kp, ki, kd });
//    }
//}

[ApiController]
[Route("[controller]")]
public class SystemController : ControllerBase
{
    private readonly ILogger<SystemController> _logger;
    private readonly SystemProxy _proxy;
    private readonly PauseContainer _pauseCont;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunResultRepo _runResultRepo;
    private readonly GeneticTunerOptions _tunerOptions;

    public SystemController(
        ILogger<SystemController> logger,
        SystemProxy proxy,
        PauseContainer pauseCont,
        IServiceScopeFactory scopeFactory,
        RunResultRepo runResultRepo,
        PuckDbContext db,
        GeneticTunerOptions tunerOptions)
    {
        _tunerOptions = tunerOptions;
        _runResultRepo = runResultRepo;
        _logger = logger;
        _proxy = proxy;
        _pauseCont = pauseCont;
        _scopeFactory = scopeFactory;
        _db = db;
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

    public record AutotuneRequest(
        double? Dt,
        int? Steps,
        double? TargetPressurePsi,
        double? MaxSafePressurePsi,
        double? MinPumpSpeed,
        double? MaxPumpSpeed
    );

    public record SimulatedAutotuneRequest(
        double? Dt,
        int? Steps,
        double? ProcessGain
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
    [Route("pressure-control/start")]
    public async Task<IActionResult> PostStartPressureControl([FromQuery] double targetPressurePsi, [FromQuery] double? minPumpSpeed, [FromQuery] double? maxPumpSpeed, CancellationToken ct = default)
    {
        var min = minPumpSpeed ?? 4.0;
        var max = maxPumpSpeed ?? 20.0;
        await _proxy.StartPressureControlAsync(targetPressurePsi, min, max, ct);
        return Ok(new { status = "started", targetPressurePsi, minPumpSpeed = min, maxPumpSpeed = max });
    }

    [HttpPost]
    [Route("pressure-control/stop")]
    public async Task<IActionResult> PostStopPressureControl(CancellationToken ct = default)
    {
        await _proxy.StopPressureControlAsync(ct);
        return Ok(new { status = "stopped" });
    }

    [HttpPost]
    [Route("pid/autotune")]
    public async Task<IActionResult> PostAutoTuneLive([FromBody] AutotuneRequest req, CancellationToken ct = default)
    {
        // Run autotune without tying it to the HTTP cancellation token to avoid request timeouts
        var result = await _proxy.AutoTunePidGeneticLiveAsync(
            CancellationToken.None,
            _tunerOptions,
            dt: req?.Dt ?? .05,
            steps: req?.Steps ?? 800,
            maxSafePressurePsi: req?.MaxSafePressurePsi ?? 50.0,
            targetPressurePsi: req?.TargetPressurePsi ?? 30.0,
            maxPumpSpeed: req?.MaxPumpSpeed ?? 14.0,
            minPumpSpeed: req?.MinPumpSpeed ?? 4.0
        );

        // Replace any previous PID profile and associated runs so only one valid set exists
        var existingProfiles = await _db.PidProfiles.ToListAsync(CancellationToken.None);
        if (existingProfiles.Count > 0)
        {
            _db.PidProfiles.RemoveRange(existingProfiles);
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        var profile = new PidProfile
        {
            Name = "Active",
            Kp = result.Kp,
            Ki = result.Ki,
            Kd = result.Kd,
            Notes = "Set by live autotune"
        };
        _db.PidProfiles.Add(profile);
        await _db.SaveChangesAsync(CancellationToken.None);

        var run = new PidAutotuneRun
        {
            PidProfileId = profile.Id,
            DtSeconds = req?.Dt ?? 0.1,
            Steps = req?.Steps ?? 120,
            TargetPressurePsi = req?.TargetPressurePsi ?? 30.0,
            MaxSafePressurePsi = req?.MaxSafePressurePsi ?? 50.0,
            MinPumpSpeed = req?.MinPumpSpeed ?? 4.0,
            MaxPumpSpeed = req?.MaxPumpSpeed ?? 14.0,
            BestKp = result.Kp,
            BestKi = result.Ki,
            BestKd = result.Kd,
            Iae = result.Iae,
            MaxOvershoot = result.MaxOvershoot,
            SteadyStateError = result.SteadyStateError,
            FinalSteadyStateError = result.FinalSteadyStateError,
            CompletedUtc = DateTime.UtcNow
        };
        _db.PidAutotuneRuns.Add(run);
        await _db.SaveChangesAsync(CancellationToken.None);

        if (result.Samples != null)
        {
            var entities = result.Samples.Select(s => new PidAutotuneSample
            {
                PidAutotuneRunId = run.Id,
                TimeSeconds = s.TimeSeconds,
                Setpoint = s.Setpoint,
                Process = s.Process,
                Output = s.Output,
                Windup = s.Windup,
                ClampedUpper = s.ClampedUpper,
                ClampedLower = s.ClampedLower
            }).ToList();
            _db.PidAutotuneSamples.AddRange(entities);
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        return Ok(new { result.Kp, result.Ki, result.Kd, Samples = result.Samples });
    }


    [HttpGet]
    [Route("io")]
    public IActionResult GetRawIoState()
    {
        var raw = _proxy.GetRawIoState();
        return Ok(raw);
    }

    private readonly PuckDbContext _db;

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
    [Route("run-status/run/{runParamsId:int}")]
    public async Task<IActionResult> PostRunStatusRun(
        [FromRoute] int runParamsId,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Posted run");

        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<RunParametersRepo>();
            var profile = await repo.GetProfileAsync(runParamsId, ct);
        if (profile == null)
            return NotFound($"Run parameters not found for id '{runParamsId}'");

        var runParams = new RunParameters
        {
            InitialPumpSpeed = profile.InitialPumpSpeed,
            GroupHeadTemperatureFarenheit = profile.GroupHeadTemperatureFarenheit,
            ThermoblockTemperatureFarenheit = profile.ThermoblockTemperatureFarenheit,
            PreExtractionTargetTemperatureFarenheit = profile.PreExtractionTargetTemperatureFarenheit,
            ExtractionWeightGrams = profile.ExtractionWeightGrams,
            MaxExtractionSeconds = profile.MaxExtractionSeconds,
            TargetPressureBar = profile.TargetPressureBar
        };
        await _proxy.RunAsync(runParams, ct);
        return Ok("Set to run");
        }
    }

    [HttpPost]
    [Route("run-status/run/default")]
    public async Task<IActionResult> PostRunStatusRunDefault(
        CancellationToken ct = default)
    {
        _logger.LogInformation("Posted run");

        RunParameters? runParams;
        using (var scope = _scopeFactory.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<RunParametersRepo>();
            runParams = repo.GetActiveParameters();
        }
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
