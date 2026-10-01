using ClawService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "ClawHub");
builder.Services.AddSingleton<ControllerManager>();
builder.Services.AddSingleton<EcManager>();
builder.Services.AddSingleton<RequestHandler>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService<Worker>();

builder.Logging.AddEventLog(o => o.SourceName = "ClawHub");

builder.Build().Run();
