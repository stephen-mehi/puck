using Puck.Services;

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
            .ToDictionary(x => x.Key, x => new TemperatureControllerProxy(prov, logger, pauseCont, x.Value));
    }

    public IReadOnlyDictionary<TemperatureControllerId, TemperatureControllerProxy> TemperatureControllers { get; }
}