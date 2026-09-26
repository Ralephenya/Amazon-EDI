using Jumbo.AmazonEdi.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jumbo.AmazonEdi.Persistence;

public class AmazonEdiDbContext : DbContext
{
    /// <summary>Everything lives under its own schema so these tables are obviously ours, whether
    /// they sit in the Jumbo Hub database or a dedicated one.</summary>
    public const string SchemaName = "AmazonEdi";

    public AmazonEdiDbContext(DbContextOptions<AmazonEdiDbContext> options) : base(options)
    {
    }

    public DbSet<InvoiceRecord> Invoices => Set<InvoiceRecord>();

    public DbSet<InvoiceLineRecord> InvoiceLines => Set<InvoiceLineRecord>();

    public DbSet<InvoiceAttemptRecord> InvoiceAttempts => Set<InvoiceAttemptRecord>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampTimestamps();
        return base.SaveChanges();
    }

    /// <summary>Timestamps are stamped here rather than by a database default, so they behave the
    /// same under SQL Server and under the SQLite the repository tests run against.</summary>
    private void StampTimestamps()
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<InvoiceRecord>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
            }

            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
            }
        }

        foreach (var entry in ChangeTracker.Entries<InvoiceAttemptRecord>().Where(e => e.State == EntityState.Added))
        {
            if (entry.Entity.CreatedAtUtc == default)
            {
                entry.Entity.CreatedAtUtc = now;
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        modelBuilder.Entity<InvoiceRecord>(entity =>
        {
            entity.ToTable("Invoice");
            entity.HasKey(invoice => invoice.Id);

            // The idempotency key. A duplicate invoice id at Amazon is a financial problem, so the
            // database refuses the second insert rather than the application checking first.
            entity.HasIndex(invoice => invoice.OmniInvoiceNumber).IsUnique();
            entity.HasIndex(invoice => invoice.Status);
            entity.HasIndex(invoice => invoice.InvoiceDate);

            entity.Property(invoice => invoice.OmniInvoiceNumber).HasMaxLength(50).IsRequired();
            entity.Property(invoice => invoice.AmazonPurchaseOrderNumber).HasMaxLength(50);
            entity.Property(invoice => invoice.CustomerAccountCode).HasMaxLength(20).IsRequired();
            entity.Property(invoice => invoice.WarehouseCode).HasMaxLength(20);
            entity.Property(invoice => invoice.CurrencyCode).HasMaxLength(3).IsRequired();
            entity.Property(invoice => invoice.TransactionId).HasMaxLength(100);
            entity.Property(invoice => invoice.ApprovedBy).HasMaxLength(100);

            // Money at (19,4). EF would otherwise pick (18,2) silently, which rounds differently
            // from Omni and would break the validator's reconciliation.
            entity.Property(invoice => invoice.TotalExcludingTax).HasPrecision(19, 4);
            entity.Property(invoice => invoice.TotalTax).HasPrecision(19, 4);
            entity.Property(invoice => invoice.TotalIncludingTax).HasPrecision(19, 4);

            // Stored as its name so the table stays readable during an incident.
            entity.Property(invoice => invoice.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

            entity.HasMany(invoice => invoice.Lines)
                .WithOne(line => line.Invoice!)
                .HasForeignKey(line => line.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(invoice => invoice.Attempts)
                .WithOne(attempt => attempt.Invoice!)
                .HasForeignKey(attempt => attempt.InvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InvoiceLineRecord>(entity =>
        {
            entity.ToTable("InvoiceLine");
            entity.HasKey(line => line.Id);
            entity.HasIndex(line => new { line.InvoiceId, line.LineNumber }).IsUnique();

            entity.Property(line => line.StockCode).HasMaxLength(50).IsRequired();
            entity.Property(line => line.Barcode).HasMaxLength(50);
            entity.Property(line => line.Asin).HasMaxLength(20);
            entity.Property(line => line.PurchaseOrderNumber).HasMaxLength(50);

            entity.Property(line => line.UnitPriceExcludingTax).HasPrecision(19, 4);
            entity.Property(line => line.LineTotalExcludingTax).HasPrecision(19, 4);
            entity.Property(line => line.LineTax).HasPrecision(19, 4);
            entity.Property(line => line.TaxRate).HasPrecision(9, 4);
        });

        modelBuilder.Entity<InvoiceAttemptRecord>(entity =>
        {
            entity.ToTable("InvoiceAttempt");
            entity.HasKey(attempt => attempt.Id);
            entity.HasIndex(attempt => attempt.InvoiceId);

            entity.Property(attempt => attempt.TransactionId).HasMaxLength(100);
            entity.Property(attempt => attempt.RequestJson).IsRequired();
        });
    }
}
