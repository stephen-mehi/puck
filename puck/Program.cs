
using Puck.Services.TemperatureController;
using Puck.Services;
using puck.Services.IoBus;
using puck.Services.PID;

var builder = WebApplication.CreateBuilder(args);

// Increase graceful shutdown time to allow logs to flush and cleanup to run during SIGTERM
builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = System.TimeSpan.FromSeconds(30));

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
    .AddSingleton<IPhoenixProxy, PhoenixProxy>()
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
      .AddSimpleConsole(o => { o.IncludeScopes = true; o.TimestampFormat = "O "; o.SingleLine = true; })
  .AddDebug()
  .SetMinimumLevel(LogLevel.Trace)
  .AddFilter("Microsoft", LogLevel.Debug)
  .AddFilter("Microsoft.AspNetCore", LogLevel.Debug)
  .AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Trace)
  .AddFilter("System.Net.Http.HttpClient", LogLevel.Information);
});



var app = builder.Build();

var reqLogger = 
    app
    .Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("Requests");

app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await next();
        reqLogger.LogInformation("HTTP {method} {path} -> {status} in {elapsed} ms",
          ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        reqLogger.LogError(ex, "Unhandled exception for {method} {path}", ctx.Request.Method, ctx.Request.Path);
        throw;
    }
});


var lifecycleLogger =
    app
    .Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("Lifecycle");

app
    .Lifetime
    .ApplicationStarted
    .Register(() =>
        lifecycleLogger.LogWarning("ApplicationStarted. PID={pid}, ENV={env}", Environment.ProcessId, app.Environment.EnvironmentName));

app
    .Lifetime
    .ApplicationStopping
    .Register(() =>
        lifecycleLogger.LogCritical("ApplicationStopping (SIGTERM likely). Dumping basic info..."));

app
    .Lifetime
    .ApplicationStopped
    .Register(() =>
        lifecycleLogger.LogCritical("ApplicationStopped"));

AppDomain.CurrentDomain.ProcessExit += (_, __) =>
  lifecycleLogger.LogCritical("ProcessExit fired");

Console.CancelKeyPress += (_, e) =>
  lifecycleLogger.LogCritical("CancelKeyPress: {key}, Cancel={cancel}", e.SpecialKey, e.Cancel);

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
