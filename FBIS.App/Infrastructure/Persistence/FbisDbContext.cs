using FBIS.App.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FBIS.App.Infrastructure.Persistence;

/// <summary>
/// Primary EF Core DbContext for the FBIS ingestion pipeline.
/// </summary>
public class FbisDbContext : DbContext
{
    public FbisDbContext(DbContextOptions<FbisDbContext> options)
        : base(options)
    {
    }

    public DbSet<TransactionRecord> TransactionRecords => Set<TransactionRecord>();

    public DbSet<TransactionRevision> TransactionRevisions => Set<TransactionRevision>();

    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TransactionRecord>(entity =>
        {
            entity.HasIndex(e => e.TransactionId).IsUnique();

            entity.Property(e => e.Amount)
                .HasPrecision(18, 2);

            entity.Property(e => e.CardLast4)
                .HasMaxLength(4);
        });

        modelBuilder.Entity<TransactionRevision>(entity =>
        {
            entity.HasOne(r => r.TransactionRecord)
                .WithMany(r => r.Revisions)
                .HasForeignKey(r => r.TransactionRecordId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
