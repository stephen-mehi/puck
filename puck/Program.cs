
using Puck.Services.TemperatureController;
using Puck.Services;
using puck.Services.IoBus;
using puck.Services.PID;

var builder = WebApplication.CreateBuilder(args);

//default run parameters
var runParams =
    new RunParameters()
    {
        InitialPumpSpeed = 8,
        MaxExtractionSeconds = 60,
        ExtractionWeightGrams = 10,
        GroupHeadTemperatureFarenheit = 100,
        ThermoblockTemperatureFarenheit = 100,
        TargetPressureBar = 5,
        PreExtractionTargetTemperatureFarenheit = 100
    };

// Add services to the container.
builder
    .Services
    .AddSingleton<ITcpIOBusConnectionFactory, PhoenixIOBusConnectionFactory>()
    .AddSingleton<PhoenixProxy>()
    .AddSingleton<FujiPXFDriverProvider>()
    .AddSingleton<TemperatureControllerConfiguration>()
    .AddSingleton<TemperatureControllerContainer>()
    .AddSingleton(new SystemProxyConfiguration(
        recircValveIO: 1,
        groupheadValveIO: 2,
        backflushValveIO: 3,
        runStatusOutputIO: 4,
        runStatusInputIO: 1,
        pumpSpeedIO: 1,
        pressureIO: 1,
        recircValveOpenDelayMs: 100,
        initialPumpSpeedDelayMs: 750,
        tempSettleTolerance: 2,
        tempSettleTimeoutSec: 30,
        pidLoopDelayMs: 500,
        mainScanLoopDelayMs: 250,
        runStateMonitorDelayMs: 250,
        pumpStopValue: 0.0,
        setAllIdleRecircOpenDelayMs: 250,
        pressureUnit: PressureUnit.Psi,
        sensorMinPressureBar: -1.0,
        sensorMaxPressureBar: 25.0,
        sensorMinCurrentmA: 4.0,
        sensorMaxCurrentmA: 20.0))
    .AddSingleton<SystemProxy>()
    .AddSingleton(new PID(kp: 1, ki: 1, kd: 1, n: 1, outputUpperLimit: 5, outputLowerLimit: 0))
    .AddSingleton<PauseContainer>()
    .AddSingleton<RunResultRepo>()
    .AddSingleton<RunParametersRepo>()
    .AddSingleton(runParams)
    .AddHostedService<SystemService>();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder
.Services
.AddLogging(builder =>
{
    builder
    .ClearProviders()
    .AddDebug()
    .AddSimpleConsole(o =>
    {
        o.IncludeScopes = true;
        o.TimestampFormat = "HH:mm:ss ";
    });

});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseCors(builder =>
        builder.WithOrigins("http://localhost:3000")
               .AllowAnyHeader()
               .AllowAnyMethod());

    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// app.UseAuthorization();
app.UseCors(x =>
{
    x
    .AllowAnyMethod()
    .AllowAnyHeader()
    .SetIsOriginAllowed(y => true)
    .AllowCredentials();
});


app.MapControllers();

app.Run();
