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
    private readonly PhoenixProxy _proxy;
    private readonly TemperatureControllerProxy _tcProxy;

    public SystemController(
        ILogger<SystemController> logger,
        TemperatureControllerProxy tcProxy,
        PhoenixProxy proxy)
    {
        _tcProxy = tcProxy;
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
    [Route("io-state")]
    public IActionResult GetIoStateAsync(CancellationToken ct = default)
    {
        var state = new
        {
            DigitalInputState = _proxy.DigitalInputState,
            DigitalOutputState = _proxy.DigitalOutputState,
            AnalogInputState = _proxy.AnalogInputState,
            AnalogOutputState = _proxy.AnalogOutputState
        };

        return Ok(state);
    }

    [HttpPost]
    [Route("digital-output")]
    public async Task<IActionResult> PostDigitalOutputAsync(CancellationToken ct = default)
    {
        bool current = _proxy.DigitalOutputState[1].Value.State;
        await _proxy.SetDigitalOutputStateAsync(1, !current, ct);

        return Ok("Set digital output");
    }

    [HttpGet]
    [Route("get-setpoint")]
    public IActionResult GetSetPoint(CancellationToken ct = default)
    {
        var sp = _tcProxy.GetSetValue();
        return Ok(sp);
    }

    [HttpGet]
    [Route("get-process-value")]
    public IActionResult GetProcessValue(CancellationToken ct = default)
    {
        var pv = _tcProxy.GetProcessValue();
        return Ok(pv);
    }

    [HttpPost]
    [Route("set-setpoint")]
    public async Task<IActionResult> SetSetPoint(CancellationToken ct = default)
    {
        var current = _tcProxy.GetSetValue();
        var next = current == 100 ? 60 : 100;
        await _tcProxy.SetSetPointAsync(next, ct);
        return Ok();
    }

}
