using AlertWebAgent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<AlertStateStore>();
builder.Services.AddSingleton<TeamsNotifier>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
