using Microsoft.AspNetCore.Server.Kestrel.Core;
using ModelSync.Core;
using ModelSync.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// The gRPC endpoint speaks HTTP/2 without TLS (h2c) on 5001; the dashboard is
// plain HTTP/1.1 on 5000. Explicit URLs (tests, --urls) override this.
if (builder.WebHost.GetSetting("urls") is null or "")
{
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ListenAnyIP(5000, listen => listen.Protocols = HttpProtocols.Http1);
        kestrel.ListenAnyIP(5001, listen => listen.Protocols = HttpProtocols.Http2);
    });
}

builder.Services.AddGrpc();
builder.Services.AddSingleton<ModelService>();
builder.Services.AddSingleton<ConflictAwarenessService>();
builder.Services.AddSingleton<OperationHub>();

var app = builder.Build();

// Instantiate the hub eagerly so it observes operations from the start.
_ = app.Services.GetRequiredService<OperationHub>();

app.MapGrpcService<ModelSyncGrpcService>();
app.MapDashboard();

app.Run();

// Exposed for in-process end-to-end tests (WebApplicationFactory).
public partial class Program;
