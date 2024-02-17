using System.ComponentModel.DataAnnotations;
using System.IO.Ports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using puck.Services;
using Puck.Services;

namespace Puck.Controllers;

[ApiController]
[Route("[controller]")]
public class SystemController : ControllerBase
{
    private readonly ILogger<SystemController> _logger;
    private readonly SystemProxy _proxy;

    public SystemController(
        ILogger<SystemController> logger,
        SystemProxy proxy)
    {
        _logger = logger;
        _proxy = proxy;
    }

    [HttpGet]
    [Route("readiness")]
    public IActionResult GetReadyState()
    {
        return Ok();
    }

    [HttpGet]
    [Route("health")]
    public IActionResult GetHealthState()
    {
        return Ok();
    }

    [HttpGet]
    [Route("state")]
    public IActionResult GetIoStateAsync(CancellationToken ct = default)
    {
        var state = new
        {
            Temperature = _proxy.GetProcessTemperature(),
            TemperatureSetPoint = _proxy.GetSetPointTemperature(),
            Pressure = _proxy.GetGroupHeadPressure(),
            PumpSpeed = _proxy.GetPumpSpeedSetting(),
            RunState = _proxy.GetRunState().ToString(),
            RecirculationValveState = _proxy.GetRecirculationValveState().ToString(),
            GroupHeadValveState = _proxy.GetGroupHeadValveState().ToString(),

        };

        return Ok(state);
    }

    [HttpPost]
    [Route("run-status/idle")]
    public async Task<IActionResult> PostRunStatusIdle(CancellationToken ct = default)
    {
        _logger.LogInformation("Posted idle");
        await _proxy.SetRunStatusIdleAsync(ct);
        return Ok("Set to idle");
    }

    [HttpPost]
    [Route("run-status/run")]
    public async Task<IActionResult> PostRunStatusRun(CancellationToken ct = default)
    {
        _logger.LogInformation("Posted run");
        await _proxy.SetRunStatusRunAsync(ct);
        return Ok("Set to run");
    }


}
