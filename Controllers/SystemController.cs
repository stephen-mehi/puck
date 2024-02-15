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
            RunState = _proxy.GetRunState(),
            RecirculationValveState = _proxy.GetRecirculationValveState(),
            GroupHeadValveState = _proxy.GetGroupHeadValveState(),
            
        };

        return Ok(state);
    }

    [HttpPost]
    [Route("run-status/idle")]
    public async Task<IActionResult> PostRunStatusIdle(CancellationToken ct = default)
    {
        await _proxy.SetRunStatusIdle(ct);
        return Ok("Set to idle");
    }

    [HttpPost]
    [Route("run-status/run")]
    public async Task<IActionResult> PostRunStatusRun(CancellationToken ct = default)
    {
        await _proxy.SetRunStatusRun(ct);
        return Ok("Set to run");
    }


}
