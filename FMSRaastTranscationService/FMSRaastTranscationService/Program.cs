using FMSRaastService;
using FMSRaastService.Models;
using FMSRaastTranscationService;
using FMSRaastTranscationService.Services;

// Load appsettings.json from the exe folder, not the current directory (console / Task Scheduler runs)
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(o => o.ServiceName = "FMSRaastTranscationService");

// Values are AES-encrypted in appsettings.json, so decrypt instead of Bind; validation runs on the plain values
builder.Services.AddOptions<RaastSettings>()
    .Configure(s => s.LoadFrom(builder.Configuration.GetSection(RaastSettings.SectionName)))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<CommonMethods>();
builder.Services.AddSingleton<ServiceRun>();
builder.Services.AddHostedService<Worker>();

builder.Build().Run();
