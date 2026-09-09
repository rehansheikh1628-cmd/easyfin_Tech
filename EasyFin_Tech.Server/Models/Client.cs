using System;
using System.Collections.Generic;

namespace EasyFin_Tech.Server.Models;

public partial class Client
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string Name { get; set; } = null!;

    public string ContactPerson { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string Phone { get; set; } = null!;

    public string BusinessName { get; set; } = null!;

    public string BusinessType { get; set; } = null!;

    public string Address { get; set; } = null!;

    public string TaxId { get; set; } = null!;

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public virtual ICollection<FileRecord> FileRecords { get; set; } = new List<FileRecord>();

    public virtual ICollection<FinancialYear> FinancialYears { get; set; } = new List<FinancialYear>();

    public virtual ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    public virtual User User { get; set; } = null!;
}
