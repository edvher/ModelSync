using ModelSync.Core;
using ModelSync.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddSingleton<OperationTree>();
builder.Services.AddSingleton<ModelManager>();
builder.Services.AddSingleton<OperationHub>();

var app = builder.Build();

app.MapGrpcService<ModelSyncGrpcService>();
app.MapGet("/", () => "ModelSync gRPC server is running. Use a gRPC client to communicate.");

app.Run();
