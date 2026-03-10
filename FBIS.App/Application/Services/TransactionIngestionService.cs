using System.Text.Json;
using System.Text.Json.Serialization;
using FBIS.App.Application.Options;
using FBIS.App.Domain.Entities;
using FBIS.App.Domain.Enums;
using FBIS.App.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FBIS.App.Application.Services;

/// <summary>
/// Coordinates ingestion of a transaction snapshot into the persistence layer.
/// </summary>
public class TransactionIngestionService
{
    private static readonly JsonSerializerOptions SnapshotSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions RevisionSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly FbisDbContext _dbContext;
    private readonly ILogger<TransactionIngestionService> _logger;
    private readonly TransactionFeedOptions _feedOptions;
    private readonly IHostEnvironment _hostEnvironment;

    public TransactionIngestionService(
        FbisDbContext dbContext,
        IOptions<TransactionFeedOptions> feedOptions,
        ILogger<TransactionIngestionService> logger,
        IHostEnvironment hostEnvironment)
    {
        _dbContext = dbContext;
        _logger = logger;
        _hostEnvironment = hostEnvironment;
        _feedOptions = feedOptions.Value;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var utcNow = DateTime.UtcNow;
        var run = new IngestionRun
        {
            Id = Guid.NewGuid(),
            StartedAt = utcNow,
        };

        var snapshotDtos = await LoadSnapshotAsync(cancellationToken);
        run.TotalProcessed = snapshotDtos.Count;

        var normalizedSnapshot = NormalizeSnapshot(snapshotDtos, out var duplicateCount);
        if (duplicateCount > 0)
        {
            _logger.LogWarning("Ignored {DuplicateCount} duplicate transaction(s) in snapshot.", duplicateCount);
        }

        var snapshotIds = normalizedSnapshot.Keys.ToHashSet(StringComparer.Ordinal);

        await using var dbTransaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var existingRecords = await _dbContext.TransactionRecords
            .Where(record => snapshotIds.Contains(record.TransactionId))
            .ToListAsync(cancellationToken);
        var existingLookup = existingRecords.ToDictionary(r => r.TransactionId, StringComparer.Ordinal);

        foreach (var snapshot in normalizedSnapshot.Values)
        {
            if (!existingLookup.TryGetValue(snapshot.TransactionId, out var record))
            {
                var entity = CreateTransactionRecord(snapshot, utcNow);
                _dbContext.TransactionRecords.Add(entity);
                existingLookup[snapshot.TransactionId] = entity;
                run.Inserted++;
                continue;
            }

            if (record.Status == TransactionStatus.Finalized)
            {
                continue;
            }

            var changed = ApplySnapshot(record, snapshot, utcNow, out var changedFields, out var previousValues);
            record.CardLast4 = snapshot.CardLast4;

            if (changed)
            {
                _dbContext.TransactionRevisions.Add(CreateRevision(record.Id, utcNow, changedFields, previousValues));
                run.Updated++;
            }
        }

        run.CompletedAt = DateTime.UtcNow;
        _dbContext.IngestionRuns.Add(run);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await dbTransaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Ingestion run completed. Processed={Processed}, Inserted={Inserted}, Updated={Updated}, Revoked={Revoked}",
            run.TotalProcessed,
            run.Inserted,
            run.Updated,
            run.Revoked);
    }

    private async Task<IReadOnlyList<TransactionSnapshotDto>> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        var path = ResolveSnapshotPath();
        _logger.LogInformation("Loading snapshot from {Path}", path);

        await using var stream = File.OpenRead(path);
        var snapshot = await JsonSerializer.DeserializeAsync<List<TransactionSnapshotDto>>(stream, SnapshotSerializerOptions, cancellationToken)
                       ?? new List<TransactionSnapshotDto>();
        return snapshot;
    }

    private string ResolveSnapshotPath()
    {
        if (string.IsNullOrWhiteSpace(_feedOptions.MockFilePath))
        {
            throw new InvalidOperationException("TransactionFeed:MockFilePath is not configured.");
        }

        if (Path.IsPathRooted(_feedOptions.MockFilePath))
        {
            return _feedOptions.MockFilePath;
        }

        var contentRoot = _hostEnvironment.ContentRootPath ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(contentRoot, _feedOptions.MockFilePath));
    }

    private static Dictionary<string, SnapshotTransaction> NormalizeSnapshot(
        IReadOnlyCollection<TransactionSnapshotDto> snapshot,
        out int duplicateCount)
    {
        var normalized = new Dictionary<string, SnapshotTransaction>(StringComparer.Ordinal);
        duplicateCount = 0;

        foreach (var dto in snapshot)
        {
            if (dto is null || string.IsNullOrWhiteSpace(dto.TransactionId))
            {
                continue;
            }

            var rawTimestamp = dto.Timestamp ?? dto.TransactionTime;
            if (rawTimestamp is null || rawTimestamp == default)
            {
                continue;
            }

            var transactionId = dto.TransactionId.Trim();
            var snapshotTransaction = new SnapshotTransaction(
                transactionId,
                GetCardLast4(dto.CardNumber),
                (dto.LocationCode ?? string.Empty).Trim(),
                (dto.ProductName ?? string.Empty).Trim(),
                dto.Amount,
                NormalizeTimestamp(rawTimestamp.Value));

            if (!normalized.TryAdd(transactionId, snapshotTransaction))
            {
                duplicateCount++;
            }
        }

        return normalized;
    }

    private static TransactionRecord CreateTransactionRecord(SnapshotTransaction snapshot, DateTime utcNow)
        => new()
        {
            Id = Guid.NewGuid(),
            TransactionId = snapshot.TransactionId,
            CardLast4 = snapshot.CardLast4,
            LocationCode = snapshot.LocationCode,
            ProductName = snapshot.ProductName,
            Amount = snapshot.Amount,
            TransactionTime = snapshot.TransactionTime,
            Status = TransactionStatus.Active,
            CreatedAt = utcNow,
            UpdatedAt = utcNow,
        };

    private static bool ApplySnapshot(
        TransactionRecord record,
        SnapshotTransaction snapshot,
        DateTime utcNow,
        out List<string> changedFields,
        out Dictionary<string, object?> previousValues)
    {
        changedFields = new List<string>();
        previousValues = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (record.Status == TransactionStatus.Revoked)
        {
            previousValues["Status"] = record.Status.ToString();
            record.Status = TransactionStatus.Active;
            changedFields.Add("Status");
        }

        if (!string.Equals(record.LocationCode, snapshot.LocationCode, StringComparison.Ordinal))
        {
            previousValues["LocationCode"] = record.LocationCode;
            record.LocationCode = snapshot.LocationCode;
            changedFields.Add("LocationCode");
        }

        if (!string.Equals(record.ProductName, snapshot.ProductName, StringComparison.Ordinal))
        {
            previousValues["ProductName"] = record.ProductName;
            record.ProductName = snapshot.ProductName;
            changedFields.Add("ProductName");
        }

        if (record.Amount != snapshot.Amount)
        {
            previousValues["Amount"] = record.Amount;
            record.Amount = snapshot.Amount;
            changedFields.Add("Amount");
        }

        if (record.TransactionTime != snapshot.TransactionTime)
        {
            previousValues["TransactionTime"] = record.TransactionTime;
            record.TransactionTime = snapshot.TransactionTime;
            changedFields.Add("TransactionTime");
        }

        if (changedFields.Count > 0)
        {
            record.UpdatedAt = utcNow;
        }

        return changedFields.Count > 0;
    }

    private static TransactionRevision CreateRevision(
        Guid recordId,
        DateTime changedAt,
        IEnumerable<string> changedFields,
        IDictionary<string, object?> previousValues)
        => new()
        {
            Id = Guid.NewGuid(),
            TransactionRecordId = recordId,
            ChangedAt = changedAt,
            ChangedFields = JsonSerializer.Serialize(changedFields, RevisionSerializerOptions),
            PreviousValues = JsonSerializer.Serialize(previousValues, RevisionSerializerOptions),
        };

    private static string GetCardLast4(string? cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber))
        {
            return string.Empty;
        }

        var digitsOnly = new string(cardNumber.Where(char.IsDigit).ToArray());
        var normalized = string.IsNullOrEmpty(digitsOnly) ? cardNumber.Trim() : digitsOnly;
        return normalized.Length <= 4 ? normalized : normalized[^4..];
    }

    private static DateTime NormalizeTimestamp(DateTime timestamp)
    {
        return timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
        };
    }

    private sealed record SnapshotTransaction(
        string TransactionId,
        string CardLast4,
        string LocationCode,
        string ProductName,
        decimal Amount,
        DateTime TransactionTime);

    private sealed record TransactionSnapshotDto
    {
        [JsonPropertyName("transactionId")]
        public string TransactionId { get; init; } = string.Empty;

        [JsonPropertyName("cardNumber")]
        public string? CardNumber { get; init; }

        [JsonPropertyName("locationCode")]
        public string? LocationCode { get; init; }

        [JsonPropertyName("productName")]
        public string? ProductName { get; init; }

        [JsonPropertyName("amount")]
        public decimal Amount { get; init; }

        [JsonPropertyName("timestamp")]
        public DateTime? Timestamp { get; init; }

        [JsonPropertyName("transactionTime")]
        public DateTime? TransactionTime { get; init; }
    }
}
