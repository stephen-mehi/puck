using Microsoft.AspNetCore.Mvc;

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

    [HttpPost(Name = "test")]
    public Task Get()
    {
    }
}
