using FBIS.App.Domain.Enums;
using FBIS.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace FBIS.Tests;

public class TransactionIngestionServiceTests
{
    [Fact]
    public async Task Insert_NewTransactions_ShouldPersistRecords()
    {
        var snapshotPath = MockSnapshotStore.CreateSnapshotFile("insert", new[]
        {
            new {
                transactionId = "T-1001",
                cardNumber = "1234567890123456",
                locationCode = "LOC-A",
                productName = "Widget",
                amount = 19.99m,
                timestamp = DateTime.UtcNow
            }
        });

        await using var database = await TestDbContextFactory.CreateAsync();

        await using (var ingestionContext = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(ingestionContext, snapshotPath);
            await service.RunAsync();
        }

        await using var assertionContext = database.CreateDbContext();
        var record = await assertionContext.TransactionRecords.SingleAsync();
        Assert.Equal("T-1001", record.TransactionId);
        Assert.Equal("3456", record.CardLast4);
        Assert.Equal(19.99m, record.Amount);
        Assert.Equal(TransactionStatus.Active, record.Status);
    }

    [Fact]
    public async Task UpdateExistingTransaction_ShouldCreateRevision()
    {
        var firstSnapshot = MockSnapshotStore.CreateSnapshotFile("update1", new[]
        {
            new {
                transactionId = "T-2001",
                cardNumber = "1111222233334444",
                locationCode = "LOC-A",
                productName = "Widget",
                amount = 10.00m,
                timestamp = DateTime.UtcNow
            }
        });

        var secondSnapshot = MockSnapshotStore.CreateSnapshotFile("update2", new[]
        {
            new {
                transactionId = "T-2001",
                cardNumber = "1111222233334444",
                locationCode = "LOC-B",
                productName = "Widget",
                amount = 12.50m,
                timestamp = DateTime.UtcNow.AddMinutes(5)
            }
        });

        await using var database = await TestDbContextFactory.CreateAsync();

        DateTime initialUpdatedAt;
        await using (var context = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(context, firstSnapshot);
            await service.RunAsync();

            var baseline = await context.TransactionRecords.AsNoTracking().SingleAsync(r => r.TransactionId == "T-2001");
            initialUpdatedAt = baseline.UpdatedAt;
        }

        await using (var context = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(context, secondSnapshot);
            await service.RunAsync();
        }

        await using var assertionContext = database.CreateDbContext();
        var record = await assertionContext.TransactionRecords.AsNoTracking().SingleAsync(r => r.TransactionId == "T-2001");
        Assert.Equal("LOC-B", record.LocationCode);
        Assert.Equal(12.50m, record.Amount);
        Assert.True(record.UpdatedAt > initialUpdatedAt, "UpdatedAt should advance when fields change.");

        var revisions = await assertionContext.TransactionRevisions.AsNoTracking().ToListAsync();
        var revision = Assert.Single(revisions);
        var changedFields = JsonSerializer.Deserialize<HashSet<string>>(revision.ChangedFields) ?? new HashSet<string>();
        Assert.Contains("LocationCode", changedFields);
        Assert.Contains("Amount", changedFields);
        Assert.Contains("TransactionTime", changedFields);

        var previousValues = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(revision.PreviousValues)
            ?? new Dictionary<string, JsonElement>();
        Assert.Equal("LOC-A", previousValues["LocationCode"].GetString());
        Assert.Equal(10.00m, previousValues["Amount"].GetDecimal());
    }

    [Fact]
    public async Task MissingTransaction_ShouldBeRevoked()
    {
        var firstSnapshot = MockSnapshotStore.CreateSnapshotFile("revoke1", new[]
        {
            new {
                transactionId = "T-3001",
                cardNumber = "4321432143214321",
                locationCode = "LOC-A",
                productName = "Widget",
                amount = 15.00m,
                timestamp = DateTime.UtcNow
            }
        });

        var secondSnapshot = MockSnapshotStore.CreateSnapshotFile("revoke2", Array.Empty<object>());

        await using var database = await TestDbContextFactory.CreateAsync();

        DateTime initialUpdatedAt;
        await using (var context = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(context, firstSnapshot);
            await service.RunAsync();

            var baseline = await context.TransactionRecords.AsNoTracking().SingleAsync(r => r.TransactionId == "T-3001");
            initialUpdatedAt = baseline.UpdatedAt;
        }

        await using (var context = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(context, secondSnapshot);
            await service.RunAsync();
        }

        await using var assertionContext = database.CreateDbContext();
        var record = await assertionContext.TransactionRecords.AsNoTracking().SingleAsync(r => r.TransactionId == "T-3001");
        Assert.Equal(TransactionStatus.Revoked, record.Status);
        Assert.True(record.UpdatedAt > initialUpdatedAt, "Revocation should update UpdatedAt.");

        var revision = await assertionContext.TransactionRevisions.AsNoTracking().SingleAsync();
        var changedFields = JsonSerializer.Deserialize<HashSet<string>>(revision.ChangedFields) ?? new HashSet<string>();
        Assert.True(changedFields.SetEquals(new[] { "Status" }), "Revocation should only change status.");

        var previousValues = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(revision.PreviousValues)
            ?? new Dictionary<string, JsonElement>();
        Assert.Equal(TransactionStatus.Active.ToString(), previousValues["Status"].GetString());
        Assert.Equal("RevokedMissingFromSnapshot", previousValues["Reason"].GetString());
    }

    [Fact]
    public async Task RunningSameSnapshotTwice_ShouldBeIdempotent()
    {
        var snapshotPath = MockSnapshotStore.CreateSnapshotFile("idempotent", new[]
        {
            new {
                transactionId = "T-4001",
                cardNumber = "1111222233334444",
                locationCode = "LOC-A",
                productName = "Widget",
                amount = 9.99m,
                timestamp = DateTime.UtcNow
            }
        });

        await using var database = await TestDbContextFactory.CreateAsync();

        await using (var context = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(context, snapshotPath);
            await service.RunAsync();
        }

        await using (var context = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(context, snapshotPath);
            await service.RunAsync();
        }

        await using var assertionContext = database.CreateDbContext();
        var recordCount = await assertionContext.TransactionRecords.CountAsync();
        var revisionCount = await assertionContext.TransactionRevisions.CountAsync();

        Assert.Equal(1, recordCount);
        Assert.Equal(0, revisionCount);
    }

    [Fact]
    public async Task TransactionsOlderThan24Hours_ShouldFinalize()
    {
        var pastTimestamp = DateTime.UtcNow.AddHours(-25);
        var initialSnapshot = MockSnapshotStore.CreateSnapshotFile("finalize-insert", new[]
        {
            new {
                transactionId = "T-5001",
                cardNumber = "5555666677778888",
                locationCode = "LOC-A",
                productName = "Widget",
                amount = 20.00m,
                timestamp = pastTimestamp
            }
        });

        var emptySnapshot = MockSnapshotStore.CreateSnapshotFile("finalize-empty", Array.Empty<object>());

        var mutatedSnapshot = MockSnapshotStore.CreateSnapshotFile("finalize-mutated", new[]
        {
            new {
                transactionId = "T-5001",
                cardNumber = "5555666677778888",
                locationCode = "LOC-B",
                productName = "Widget PRO",
                amount = 50.00m,
                timestamp = DateTime.UtcNow
            }
        });

        await using var database = await TestDbContextFactory.CreateAsync();

        DateTime initialUpdatedAt;
        await using (var context = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(context, initialSnapshot);
            await service.RunAsync();

            var inserted = await context.TransactionRecords.AsNoTracking().SingleAsync(r => r.TransactionId == "T-5001");
            initialUpdatedAt = inserted.UpdatedAt;
        }

        DateTime finalizedUpdatedAt;
        await using (var context = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(context, emptySnapshot);
            await service.RunAsync();

            var finalized = await context.TransactionRecords.AsNoTracking().SingleAsync(r => r.TransactionId == "T-5001");
            Assert.Equal(TransactionStatus.Finalized, finalized.Status);
            Assert.True(finalized.UpdatedAt > initialUpdatedAt, "Finalization should advance UpdatedAt.");
            finalizedUpdatedAt = finalized.UpdatedAt;
        }

        await using (var context = database.CreateDbContext())
        {
            var service = database.CreateIngestionService(context, mutatedSnapshot);
            await service.RunAsync();
        }

        await using var assertionContext = database.CreateDbContext();
        var record = await assertionContext.TransactionRecords.AsNoTracking().SingleAsync(r => r.TransactionId == "T-5001");
        Assert.Equal(TransactionStatus.Finalized, record.Status);
        Assert.Equal("LOC-A", record.LocationCode);
        Assert.Equal(20.00m, record.Amount);
        Assert.Equal(finalizedUpdatedAt, record.UpdatedAt);

        var revisions = await assertionContext.TransactionRevisions.AsNoTracking().ToListAsync();
        var revision = Assert.Single(revisions);
        var changedFields = JsonSerializer.Deserialize<HashSet<string>>(revision.ChangedFields) ?? new HashSet<string>();
        Assert.True(changedFields.SetEquals(new[] { "Status" }), "Finalization should only change status.");

        var previousValues = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(revision.PreviousValues)
            ?? new Dictionary<string, JsonElement>();
        Assert.Equal(TransactionStatus.Active.ToString(), previousValues["Status"].GetString());
        Assert.Equal("FinalizedAfter24Hours", previousValues["Reason"].GetString());
    }
}
