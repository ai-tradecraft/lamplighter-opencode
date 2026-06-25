using ChatThroughHarness.Protocol;
using ChatThroughHarness.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<RunnerOptions>(builder.Configuration.GetSection("Runner"));
builder.Services.AddHostedService<RunnerWorker>();

await builder.Build().RunAsync();
