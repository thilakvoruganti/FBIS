using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using FBIS.App.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace FBIS.App.Domain.Entities;

[Index(nameof(TransactionId), IsUnique = true)]
public class TransactionRecord
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string TransactionId { get; set; } = string.Empty;

    [Required]
    [MaxLength(4)]
    public string CardLast4 { get; set; } = string.Empty;

    [Required]
    public string LocationCode { get; set; } = string.Empty;

    [Required]
    public string ProductName { get; set; } = string.Empty;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public DateTime TransactionTime { get; set; }

    public TransactionStatus Status { get; set; } = TransactionStatus.Active;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<TransactionRevision> Revisions { get; set; } = new List<TransactionRevision>();
}
