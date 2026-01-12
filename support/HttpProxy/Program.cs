using ChaosWorkshop.HttpProxy.Features.Chaos;
using ChaosWorkshop.HttpProxy.Features.Shared.Observability;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.docker.json", optional: true);

builder.Logging.AddConfiguredOpenTelemetry();
builder.Services.AddConfiguredOpenTelemetry();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransformFactory<ChaosTransformFactory>();

var app = builder.Build();

app.MapReverseProxy();

app.MapGet("/", () => "Hello World!");

app.Run();
