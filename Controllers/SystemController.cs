using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using puck.Phoenix;

namespace puck.Controllers;

[ApiController]
[Route("[controller]")]
public class SystemController : ControllerBase
{
    private readonly ILogger<SystemController> _logger;

    public SystemController(ILogger<SystemController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Test(CancellationToken ct = default)
    {
        var fact = new PhoenixIOBusConnectionFactory();

        using(var phoenix = await (fact.ConnectAsync(
            "169.254.34.100", 
            TimeSpan.FromSeconds(3), 
            TimeSpan.FromSeconds(1), 
            new Dictionary<ushort, AnalogInputConfig>(), 
            new Dictionary<ushort, AnalogMeasurementRange>(),
            ct: ct)))
        {
            bool currentState = await phoenix.ReadDigitalOutputAsync(1, ct);
            await phoenix.WriteDigitalOutputAsync(1, !currentState, ct);
            return Ok($"Changed output 1 to: {!currentState}");
        }
    }
}
