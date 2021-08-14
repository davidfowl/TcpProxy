var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseDelegatedTransport();

var app = builder.Build();

app.MapGet("/", () => $"From {Environment.ProcessId}");

app.Run();
