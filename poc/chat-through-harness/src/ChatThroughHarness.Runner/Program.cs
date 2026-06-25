using ChatThroughHarness.Protocol;
using ChatThroughHarness.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<RunnerOptions>(builder.Configuration.GetSection("Runner"));
builder.Services.AddHttpClient<RunnerApiClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<RunnerOptions>>().Value;
    client.BaseAddress = options.OrchestratorBaseUri;
});
builder.Services.AddTransient<IRunnerApiClient>(sp => sp.GetRequiredService<RunnerApiClient>());
builder.Services.AddSingleton<IHarnessProcessRunner, HarnessProcessRunner>();
builder.Services.AddSingleton<IRunnerCommandHandler, CliRunnerCommandHandler>();
builder.Services.AddSingleton<RunnerCommandLoop>();
builder.Services.AddHostedService<RunnerWorker>();

await builder.Build().RunAsync();
