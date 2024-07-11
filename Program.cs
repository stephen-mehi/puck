using puck.Services;
using Puck.Services;

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
    .AddSingleton<TemperatureControllerProxy>()
    .AddSingleton<TemperatureControllerConfiguration>()
    .AddSingleton<TemperatureControllerContainer>()
    .AddSingleton<SystemProxy>()
    .AddSingleton(new PID(kp: 1, ki: 1, kd: 1, n: 1, outputUpperLimit: 6, outputLowerLimit: 1))
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
    app.UseSwagger();
    app.UseSwaggerUI();
}

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
