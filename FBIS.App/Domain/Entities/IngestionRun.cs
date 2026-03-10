using System.ComponentModel.DataAnnotations;

namespace FBIS.App.Domain.Entities;

public class IngestionRun
{
    [Key]
    public Guid Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime CompletedAt { get; set; }

    public int TotalProcessed { get; set; }

    public int Inserted { get; set; }

    public int Updated { get; set; }

    public int Revoked { get; set; }
}
