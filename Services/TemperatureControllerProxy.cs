namespace Puck.Services;

public class TemperatureControllerProxy
{
    public async Task<double> Test()
    {
        var portConfig = new
            FujiPXFDriverPortConfiguration(
                "/dev/ttyUSB0",
                TimeSpan.FromSeconds(3));

        var test =
            await (new FujiPXFDriverProvider())
            .ConnectAsync(portConfig);

        return await test.GetProcessValueAsync();
    }
}