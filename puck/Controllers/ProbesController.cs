using System.ComponentModel.DataAnnotations;
using System.IO.Ports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Puck.Services;

namespace Puck.Controllers;

[ApiController]
public class ProbesController : ControllerBase
{

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
}
