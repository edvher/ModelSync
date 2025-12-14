using ModelSync.Server.Components;
using ModelSync.Core;
using ModelSync.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<OperationTree>();
builder.Services.AddSingleton<ModelManager>();
builder.Services.AddSingleton<OperationHub>();
builder.Services.AddSingleton<OperationDashboardService>();

var app = builder.Build();

app.MapStaticAssets();
app.UseStaticFiles();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGrpcService<ModelSyncGrpcService>();
app.MapGet("/health", () => "ModelSync gRPC server is running. Use a gRPC client to communicate.");

app.Run();
