using ChatThroughHarness.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services
    .AddOptions<RunnerOptions>()
    .Bind(builder.Configuration.GetSection("Runner"))
    .PostConfigure(options =>
    {
        options.ControllerWorkspace = ControllerWorkspaceResolver.ResolveFromEnvironment();
        ControllerWorkspaceResolver.EnsureWritable(options.ControllerWorkspace);
    });
builder.Services.AddHttpClient<RunnerApiClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<RunnerOptions>>().Value;
    client.BaseAddress = options.OrchestratorBaseUri;
});
builder.Services.AddTransient<IRunnerApiClient>(sp => sp.GetRequiredService<RunnerApiClient>());
builder.Services.AddHttpClient<IOpenCodeHealthProbe, OpenCodeHealthProbe>();
builder.Services.AddSingleton<IHarnessProcessRunner, HarnessProcessRunner>();
builder.Services.AddSingleton<IRunnerCommandHandler, CliRunnerCommandHandler>();
builder.Services.AddSingleton<RunnerCommandLoop>();
builder.Services.AddHostedService<RunnerWorker>();

await builder.Build().RunAsync();
