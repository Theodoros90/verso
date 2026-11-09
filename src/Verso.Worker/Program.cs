using Verso.Infrastructure;
using Verso.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddVersoPersistence(builder.Configuration);
builder.Services.AddHostedService<WorkerHeartbeat>();

var host = builder.Build();
host.Run();
