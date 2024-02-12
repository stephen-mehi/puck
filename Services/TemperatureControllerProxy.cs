namespace Puck.Services;

public class TemperatureControllerProxy
{
    private readonly FujiPXFDriver _driver;
    public TemperatureControllerProxy()
    {

    }

    public async Task<double> Test()
    {
        var portConfig = new
            FujiPXFDriverPortConfiguration(
                "/dev/ttyUSB0",
                TimeSpan.FromSeconds(3));

        using (var driver = await (new FujiPXFDriverProvider()).ConnectAsync(portConfig))
        {
            var val = await driver.GetProcessValueAsync();
            return val;
        }
    }
}