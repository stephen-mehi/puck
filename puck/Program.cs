
using Puck.Services.TemperatureController;
using Puck.Services;
using puck.Services.IoBus;
using puck.Services.PID;
using Microsoft.EntityFrameworkCore;
using Puck.Services.Persistence;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Emit an early bootstrap marker so we can verify new images are running even before the host starts
var asm = typeof(Program).Assembly;
var asmVersion = asm.GetName().Version?.ToString() ?? "unknown";
var asmInfoVersion = System.Reflection.Assembly
    .GetExecutingAssembly()
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
    .FirstOrDefault()?.InformationalVersion;
var versionForLog = asmInfoVersion ?? asmVersion;
Console.WriteLine($"DEPLOY_MARKER: Puck starting. Version={versionForLog} UTC={DateTime.UtcNow:o}");

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
    .AddDbContext<PuckDbContext>(options =>
    {
        // SQLite file path (env override) e.g., /data/puck.db
        var dbPath = Environment.GetEnvironmentVariable("DB_PATH") ?? "puck.db";
        var cs = $"Data Source={dbPath};Cache=Shared";
        options.UseSqlite(cs);
    })
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
        pumpStopValue: 4,
        setAllIdleRecircOpenDelayMs: 250,
        pressureUnit: PressureUnit.Psi,
        sensorMinPressurePsi: 0,
        sensorMaxPressurePsi: 150,
        sensorMinCurrentmA: 4.0,
        sensorMaxCurrentmA: 20.0))
    .AddSingleton<SystemProxy>()
    .AddSingleton(sp =>
    {
        // Resolve PID gains from DB in an isolated scope to avoid capturing scoped services
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PuckDbContext>();
        var profile = db.PidProfiles.OrderByDescending(p => p.CreatedUtc).FirstOrDefault();
        var kp = profile?.Kp ?? 1.0;
        var ki = profile?.Ki ?? 1.0;
        var kd = profile?.Kd ?? 1.0;
        // Pump output bounds: match current defaults (min 4, max 20)
        return new PID(kp: kp, ki: ki, kd: kd, n: 1, outputUpperLimit: 20, outputLowerLimit: 4);
    })
    .AddSingleton<PauseContainer>()
    .AddSingleton<RunResultRepo>()
    .AddScoped<RunParametersRepo>()
    .AddSingleton(runParams)
    .AddHostedService<SystemService>();


builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder
.Services
.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddSimpleConsole(o =>
    {
        o.IncludeScopes = false;
        o.TimestampFormat = "HH:mm:ss ";
        o.SingleLine = true;
    });
    logging.SetMinimumLevel(LogLevel.Information);
    logging.AddFilter("Microsoft", LogLevel.Warning);
    logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
    logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
});



var app = builder.Build();

// Ensure database schema is created/migrated
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PuckDbContext>();
    var hasMigrations = db.Database.GetMigrations().Any();
    if (hasMigrations)
        db.Database.Migrate();
    else
        db.Database.EnsureCreated();
}

var reqLogger = 
    app
    .Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("Requests");

app.Use(async (ctx, next) =>
{
    try
    {
        await next();
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
        lifecycleLogger.LogInformation("ApplicationStarted. PID={pid}, ENV={env}, Version={ver}",
            Environment.ProcessId,
            app.Environment.EnvironmentName,
            versionForLog));

app
    .Lifetime
    .ApplicationStopping
    .Register(() =>
        lifecycleLogger.LogInformation("ApplicationStopping (SIGTERM likely)"));

app
    .Lifetime
    .ApplicationStopped
    .Register(() =>
        lifecycleLogger.LogInformation("ApplicationStopped"));

AppDomain.CurrentDomain.ProcessExit += (_, __) =>
  lifecycleLogger.LogInformation("ProcessExit fired");

Console.CancelKeyPress += (_, e) =>
  lifecycleLogger.LogInformation("CancelKeyPress: {key}, Cancel={cancel}", e.SpecialKey, e.Cancel);

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
