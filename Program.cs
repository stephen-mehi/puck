using Puck.Services;

var builder = WebApplication.CreateBuilder(args);

ITcpIOBusConnectionFactory connectFact = new PhoenixIOBusConnectionFactory();
var phoenix = new PhoenixProxy(connectFact);

var tcProxy = new TemperatureControllerProxy();

// Add services to the container.
builder
    .Services
    .AddSingleton(phoenix)
    .AddSingleton(tcProxy);
// .AddHostedService<SystemService>();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddLogging(builder =>
{
    builder
    .ClearProviders()
    .AddDebug()
    .AddConsole();
    
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
