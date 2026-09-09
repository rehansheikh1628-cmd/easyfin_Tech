using System;
using System.Collections.Generic;
using EasyFin_Tech.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace EasyFin_Tech.Server.Data;

public partial class EasyFinDbContext : DbContext
{
    public EasyFinDbContext(DbContextOptions<EasyFinDbContext> options)
        : base(options)
    {
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
        });

        modelBuilder.Entity<FinancialYear>(entity =>
        {
            entity.HasIndex(e => new { e.ClientId, e.DisplayName }, "IX_FinancialYears_ClientId_DisplayName").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.Client).WithMany(p => p.FinancialYears).HasForeignKey(d => d.ClientId);
        });

        modelBuilder.Entity<PdfProcessingResult>(entity =>
        {
            entity.HasIndex(e => e.FileRecordId, "IX_PdfProcessingResults_FileRecordId").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.FileRecord).WithOne(p => p.PdfProcessingResult).HasForeignKey<PdfProcessingResult>(d => d.FileRecordId);
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
        });

        modelBuilder.Entity<TransactionImportResult>(entity =>
        {
            entity.HasIndex(e => e.SourceFileId, "IX_TransactionImportResults_SourceFileId").IsUnique();

            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.HasOne(d => d.SourceFile).WithOne(p => p.TransactionImportResult).HasForeignKey<TransactionImportResult>(d => d.SourceFileId);
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
