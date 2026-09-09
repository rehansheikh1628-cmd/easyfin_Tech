using System;
using System.Collections.Generic;

namespace EasyFin_Tech.Server.Models;

public partial class FinancialYear
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public string DisplayName { get; set; } = null!;

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public int Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual Client Client { get; set; } = null!;

    public virtual ICollection<FileRecord> FileRecords { get; set; } = new List<FileRecord>();

    public virtual ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
