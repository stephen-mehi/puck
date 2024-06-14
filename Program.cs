using puck.Services;
using Puck.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder
    .Services
    .AddSingleton<ITcpIOBusConnectionFactory, PhoenixIOBusConnectionFactory>()
    .AddSingleton<PhoenixProxy>()
    .AddSingleton<FujiPXFDriverProvider>()
    .AddSingleton<TemperatureControllerProxy>()
    .AddSingleton<SystemProxy>()
    .AddSingleton<PauseContainer>()
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
