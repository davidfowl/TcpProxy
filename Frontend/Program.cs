
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseDelegatingTransport(o =>
{
    // Path to the backend process we'll launch to delegate connections to
    o.ProcessPath = Path.Combine("..", "Backend", "bin", "Debug", "net6.0", "Backend.exe");
});

var app = builder.Build();

app.MapGet("/", () => $"From {Environment.ProcessId}");

app.Run();
