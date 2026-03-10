using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FBIS.App.Domain.Entities;

public class TransactionRevision
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid TransactionRecordId { get; set; }

    public DateTime ChangedAt { get; set; }

    [Required]
    public string ChangedFields { get; set; } = string.Empty;

    [Required]
    public string PreviousValues { get; set; } = string.Empty;

    [ForeignKey(nameof(TransactionRecordId))]
    public TransactionRecord? TransactionRecord { get; set; }
}
