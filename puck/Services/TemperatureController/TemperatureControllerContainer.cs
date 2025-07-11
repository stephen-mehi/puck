using Puck.Services.TemperatureController;
using Puck.Services;
using puck.Services.TemperatureController;
using System.Collections.Generic;

public class TemperatureControllerContainer
{
    public TemperatureControllerContainer(
        TemperatureControllerConfiguration tempConfig,
        FujiPXFDriverProvider prov,
        ILogger<TemperatureControllerProxy> logger,
        PauseContainer pauseCont)
    {
        TemperatureControllers =
            tempConfig
            .PortMap
            .ToDictionary(x => x.Key, x => (ITemperatureController)new TemperatureControllerProxy(prov, logger, pauseCont, x.Value));
    }

    // Test injection constructor
    public TemperatureControllerContainer(IReadOnlyDictionary<TemperatureControllerId, ITemperatureController> controllers)
    {
        TemperatureControllers = controllers;
    }

    public IReadOnlyDictionary<TemperatureControllerId, ITemperatureController> TemperatureControllers { get; }
}