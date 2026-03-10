using FBIS.App.Application.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Wire up configuration-backed options for downstream services.
builder.Services.Configure<TransactionFeedOptions>(builder.Configuration.GetSection("TransactionFeed"));
builder.Services.Configure<JobOptions>(builder.Configuration.GetSection("Job"));

using var host = builder.Build();

var logger = host.Services
	.GetRequiredService<ILoggerFactory>()
	.CreateLogger("FBIS");

logger.LogInformation("FBIS console application bootstrapped.");

await host.RunAsync();
