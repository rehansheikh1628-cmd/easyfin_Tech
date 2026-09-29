using System;
using System.Collections.Generic;
using System.Security.Claims;
using Accufex.Server.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Accufex.Server.Data;

public partial class AccufexDbContext : DbContext
{
    private readonly IHttpContextAccessor? _httpContextAccessor;
    private Guid? _tenantUserId;

    public AccufexDbContext(
        DbContextOptions<AccufexDbContext> options,
        IHttpContextAccessor? httpContextAccessor = null)
        : base(options)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Evaluates the active tenant identity for global query filtering.
    /// Prefers explicit override if SetTenantUserId was called (e.g. for background/system jobs);
    /// otherwise dynamically resolves the authenticated user ID from HttpContext claims.
    /// Returns null if unauthenticated or running outside an HTTP request.
    /// </summary>
    public Guid? CurrentUserId => _tenantUserId ?? ResolveUserIdFromHttpContext();

    /// <summary>
    /// Explicitly sets the tenant context for this DbContext scope.
    /// Useful for background workers, system maintenance, or unit testing.
    /// </summary>
    public void SetTenantUserId(Guid? userId)
    {
        _tenantUserId = userId;
    }

    private Guid? ResolveUserIdFromHttpContext()
    {
        var claim = _httpContextAccessor?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier);
        if (claim != null && Guid.TryParse(claim.Value, out var guid))
        {
            return guid;
        }
        return null;
    }

    public virtual DbSet<Client> Clients { get; set; }

    public virtual DbSet<FileRecord> FileRecords { get; set; }

    public virtual DbSet<FinancialYear> FinancialYears { get; set; }

    public virtual DbSet<PdfProcessingResult> PdfProcessingResults { get; set; }

    public virtual DbSet<Transaction> Transactions { get; set; }

    public virtual DbSet<TransactionImportResult> TransactionImportResults { get; set; }

    public virtual DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasIndex(e => e.UserId, "IX_Clients_UserId");

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.User).WithMany(p => p.Clients).HasForeignKey(d => d.UserId);

            // EF Core Global Query Filter: Scopes client workspaces to authenticated user
            entity.HasQueryFilter(c => CurrentUserId == null || c.UserId == CurrentUserId);
        });

        modelBuilder.Entity<FileRecord>(entity =>
        {
            entity.HasIndex(e => e.ClientId, "IX_FileRecords_ClientId");

            entity.HasIndex(e => e.FinancialYearId, "IX_FileRecords_FinancialYearId");

            entity.HasIndex(e => e.ProcessingStatus, "IX_FileRecords_ProcessingStatus");

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.Client).WithMany(p => p.FileRecords)
                .HasForeignKey(d => d.ClientId)
                .OnDelete(DeleteBehavior.ClientSetNull);

            entity.HasOne(d => d.FinancialYear).WithMany(p => p.FileRecords).HasForeignKey(d => d.FinancialYearId);

            // EF Core Global Query Filter: Scopes bank statements to clients owned by authenticated user
            entity.HasQueryFilter(f => CurrentUserId == null || f.Client.UserId == CurrentUserId);
        });

        modelBuilder.Entity<FinancialYear>(entity =>
        {
            entity.HasIndex(e => new { e.ClientId, e.DisplayName }, "IX_FinancialYears_ClientId_DisplayName").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.Client).WithMany(p => p.FinancialYears).HasForeignKey(d => d.ClientId);

            // EF Core Global Query Filter: Scopes accounting years to clients owned by authenticated user
            entity.HasQueryFilter(fy => CurrentUserId == null || fy.Client.UserId == CurrentUserId);
        });

        modelBuilder.Entity<PdfProcessingResult>(entity =>
        {
            entity.HasIndex(e => e.FileRecordId, "IX_PdfProcessingResults_FileRecordId").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.FileRecord).WithOne(p => p.PdfProcessingResult).HasForeignKey<PdfProcessingResult>(d => d.FileRecordId);

            // EF Core Global Query Filter: Scopes PDF extraction results to statements owned by authenticated user
            entity.HasQueryFilter(p => CurrentUserId == null || FileRecords.Any(f => f.Id == p.FileRecordId));
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasIndex(e => e.BankCode, "IX_Transactions_BankCode");

            entity.HasIndex(e => e.ClientId, "IX_Transactions_ClientId");

            entity.HasIndex(e => e.FinancialYearId, "IX_Transactions_FinancialYearId");

            entity.HasIndex(e => e.SourceFileId, "IX_Transactions_SourceFileId");

            entity.HasIndex(e => e.TransactionDate, "IX_Transactions_TransactionDate");

            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.Property(e => e.Amount).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Balance).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Credit).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Debit).HasColumnType("decimal(18, 2)");
            entity.Property(e => e.Utr).HasColumnName("UTR");

            entity.HasOne(d => d.Client).WithMany(p => p.Transactions)
                .HasForeignKey(d => d.ClientId)
                .OnDelete(DeleteBehavior.ClientSetNull);

            entity.HasOne(d => d.FinancialYear).WithMany(p => p.Transactions)
                .HasForeignKey(d => d.FinancialYearId)
                .OnDelete(DeleteBehavior.ClientSetNull);

            entity.HasOne(d => d.SourceFile).WithMany(p => p.Transactions).HasForeignKey(d => d.SourceFileId);

            // EF Core Global Query Filter: Scopes financial transactions to statements owned by authenticated user
            entity.HasQueryFilter(t => CurrentUserId == null || FileRecords.Any(f => f.Id == t.SourceFileId));
        });

        modelBuilder.Entity<TransactionImportResult>(entity =>
        {
            entity.HasIndex(e => e.SourceFileId, "IX_TransactionImportResults_SourceFileId").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.SourceFile).WithOne(p => p.TransactionImportResult).HasForeignKey<TransactionImportResult>(d => d.SourceFileId);

            // EF Core Global Query Filter: Scopes transaction import summaries to statements owned by authenticated user
            entity.HasQueryFilter(r => CurrentUserId == null || FileRecords.Any(f => f.Id == r.SourceFileId));
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(e => e.Email, "IX_Users_Email").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
