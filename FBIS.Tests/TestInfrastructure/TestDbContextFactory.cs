using FBIS.App.Application.Options;
using FBIS.App.Application.Services;
using FBIS.App.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FBIS.Tests.TestInfrastructure;

/// <summary>
/// Provides helpers for creating shared in-memory SQLite databases for ingestion tests.
/// </summary>
public sealed class TestDbContextFactory : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILoggerFactory _loggerFactory;

    private TestDbContextFactory(SqliteConnection connection, ILoggerFactory loggerFactory)
    {
        _connection = connection;
        _loggerFactory = loggerFactory;
    }

    public static async Task<TestDbContextFactory> CreateAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:;Mode=Memory;Cache=Shared");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<FbisDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var context = new FbisDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());
        return new TestDbContextFactory(connection, loggerFactory);
    }

    public FbisDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<FbisDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new FbisDbContext(options);
    }

    public TransactionIngestionService CreateIngestionService(FbisDbContext context, string snapshotPath)
    {
        var hostEnvironment = new TestHostEnvironment(Path.GetDirectoryName(snapshotPath) ?? Directory.GetCurrentDirectory());
        var feedOptions = Options.Create(new TransactionFeedOptions
        {
            MockFilePath = snapshotPath,
            Mode = "MockFile",
        });

        var logger = _loggerFactory.CreateLogger<TransactionIngestionService>();
        return new TransactionIngestionService(context, feedOptions, logger, hostEnvironment);
    }

    public async ValueTask DisposeAsync()
    {
        _loggerFactory.Dispose();
        await _connection.DisposeAsync();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "FBIS.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
