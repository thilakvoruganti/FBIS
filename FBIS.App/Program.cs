using FBIS.App.Application.Options;
using FBIS.App.Application.Services;
using FBIS.App.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;

var contentRootPath = ResolveContentRoot();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
	Args = args,
	ContentRootPath = contentRootPath
});

// Wire up configuration-backed options for downstream services.
builder.Services.Configure<TransactionFeedOptions>(builder.Configuration.GetSection("TransactionFeed"));
builder.Services.Configure<JobOptions>(builder.Configuration.GetSection("Job"));

var databasePath = Path.Combine(builder.Environment.ContentRootPath, "fbis.db");
var connectionString = $"Data Source={databasePath}";

builder.Services.AddDbContext<FbisDbContext>(options =>
	options.UseSqlite(connectionString));

builder.Services.AddScoped<TransactionIngestionService>();

using var host = builder.Build();

await InitializeDatabaseAsync(host.Services, databasePath);

await host.StartAsync();

using (var scope = host.Services.CreateScope())
{
	var ingestionService = scope.ServiceProvider.GetRequiredService<TransactionIngestionService>();
	await ingestionService.RunAsync();
}

await host.StopAsync();

static async Task InitializeDatabaseAsync(IServiceProvider services, string databasePath)
{
	using var scope = services.CreateScope();
	var scopedServices = scope.ServiceProvider;
	var logger = scopedServices
		.GetRequiredService<ILoggerFactory>()
		.CreateLogger("DatabaseInitialization");
	var dbContext = scopedServices.GetRequiredService<FbisDbContext>();

	try
	{
		logger.LogInformation("Ensuring database exists at {DatabasePath}.", databasePath);
		await dbContext.Database.MigrateAsync();
		logger.LogInformation("Database migrations applied successfully.");
	}
	catch (Exception ex)
	{
		logger.LogError(ex, "An error occurred while initializing the database located at {DatabasePath}.", databasePath);
		throw;
	}
}

static string ResolveContentRoot()
{
	var candidate = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
	var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	var folderName = Path.GetFileName(normalizedCandidate);

	if (Directory.Exists(candidate) && string.Equals(folderName, "FBIS.App", StringComparison.Ordinal))
	{
		return candidate;
	}

	throw new InvalidOperationException($"Unable to resolve FBIS.App content root from base directory '{AppContext.BaseDirectory}'.");
}
