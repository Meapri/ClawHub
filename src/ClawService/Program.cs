using ClawService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "ClawHub");
// 백그라운드 작업 하나의 예외로 호스트 전체(=EC 재적용, 버튼 감시)가 멈추지 않게
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
builder.Services.AddSingleton<PowerManager>();
builder.Services.AddSingleton<EnduranceManager>();
builder.Services.AddSingleton<ControllerManager>();
builder.Services.AddSingleton<EcManager>();
builder.Services.AddSingleton<MsiCenterManager>();
builder.Services.AddSingleton<FrameMonitor>();
builder.Services.AddSingleton<SystemManager>();
builder.Services.AddSingleton<DisplayQualityManager>();
builder.Services.AddSingleton<RequestHandler>();
builder.Services.AddSingleton<PipeServer>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<PowerHostedService>();

builder.Logging.AddEventLog(o => o.SourceName = "ClawHub");

builder.Build().Run();
