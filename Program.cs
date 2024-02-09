using Puck.Services;

var builder = WebApplication.CreateBuilder(args);

ITcpIOBusConnectionFactory connectFact = new PhoenixIOBusConnectionFactory();
var phoenix = new PhoenixProxy(connectFact);

// Add services to the container.
builder
    .Services
    .AddSingleton(phoenix);
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

app.UseAuthorization();

app.MapControllers();

app.Run();
