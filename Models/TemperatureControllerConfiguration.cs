using puck.Services;

public class TemperatureControllerConfiguration
{
    public IReadOnlyDictionary<TemperatureControllerId, string> PortMap { get; } =
        new Dictionary<TemperatureControllerId, string>()
        {
            {TemperatureControllerId.GroupHead, "/dev/ttyUSB1"},
            {TemperatureControllerId.ThermoBlock, "/dev/ttyUSB0"},
        };
}