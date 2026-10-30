using FitCore.Application.Core;
using FitCore.Invoicing.Domain;
using FitCore.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FitCore.Invoicing.Persistence;

/// <summary>
/// Contexto del módulo de facturación electrónica. Todo lleva <c>tenant_id</c>, filtro global y Row-Level Security forzada, como el resto de módulos de
/// negocio. Sus secretos (certificados) se guardan cifrados y nunca se auditan ni se devuelven.
/// </summary>
public sealed class InvoicingDbContext(DbContextOptions<InvoicingDbContext> options, ITenantContext tenantContext) : DbContext(options)
{
    public const string Schema = "invoicing";

    internal DbSet<IssuerProfile> Issuers => Set<IssuerProfile>();
    internal DbSet<EmissionPoint> EmissionPoints => Set<EmissionPoint>();
    internal DbSet<SigningCertificate> Certificates => Set<SigningCertificate>();
    internal DbSet<Invoice> Invoices => Set<Invoice>();
    internal DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    internal DbSet<InvoiceTax> InvoiceTaxes => Set<InvoiceTax>();
    internal DbSet<InvoicePayment> InvoicePayments => Set<InvoicePayment>();
    internal DbSet<InvoiceEvent> InvoiceEvents => Set<InvoiceEvent>();
    internal DbSet<InvoiceDocument> InvoiceDocuments => Set<InvoiceDocument>();
    internal DbSet<EmissionSequence> EmissionSequences => Set<EmissionSequence>();
    internal DbSet<InvoiceTransmission> Transmissions => Set<InvoiceTransmission>();
    internal DbSet<InvoiceSriAttempt> SriAttempts => Set<InvoiceSriAttempt>();
    internal DbSet<BranchRef> Branches => Set<BranchRef>();

    // Guid.Empty nunca coincide con un gimnasio real: sin tenant activo no se ve ninguna fila.
    internal Guid CurrentTenantId => tenantContext.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<IssuerProfile>(b =>
        {
            b.ToTable("issuer_profiles");
            b.HasKey(x => x.TenantId);
            b.Property(x => x.TenantId).ValueGeneratedNever();
            b.Property(x => x.Ruc).HasMaxLength(13).IsFixedLength().IsRequired();
            b.Property(x => x.LegalName).HasMaxLength(300).IsRequired();
            b.Property(x => x.TradeName).HasMaxLength(300);
            b.Property(x => x.HeadOfficeAddress).HasMaxLength(300).IsRequired();
            b.Property(x => x.TaxRegime).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.SpecialTaxpayerNumber).HasMaxLength(13);
            b.Property(x => x.WithholdingAgentNumber).HasMaxLength(13);
            b.Property(x => x.Environment).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.EmissionType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.NotificationEmail).HasMaxLength(256);
            b.Property(x => x.CardPaymentMethod).HasConversion<string>().HasMaxLength(10).HasDefaultValue(CardPaymentMethod.Credit);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<EmissionPoint>(b =>
        {
            b.ToTable("emission_points");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.EstablishmentCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.PointCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.EstablishmentAddress).HasMaxLength(300).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.EstablishmentCode, x.PointCode }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<SigningCertificate>(b =>
        {
            b.ToTable("signing_certificates");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Subject).HasMaxLength(500).IsRequired();
            b.Property(x => x.Issuer).HasMaxLength(500).IsRequired();
            b.Property(x => x.SerialNumber).HasMaxLength(100).IsRequired();
            b.Property(x => x.Thumbprint).HasMaxLength(64).IsFixedLength().IsRequired();
            b.Property(x => x.SubjectRuc).HasMaxLength(13);
            b.Property(x => x.EncryptedP12).HasColumnType("bytea");
            b.Property(x => x.EncryptedPassword).HasColumnType("bytea");
            b.HasIndex(x => x.TenantId).HasFilter("is_active").IsUnique();
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("invoices");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.SourceType).HasMaxLength(30).IsRequired();
            b.Property(x => x.SourceReference).HasMaxLength(60).IsRequired();
            b.Property(x => x.EstablishmentCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.PointCode).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.Environment).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.AccessKey).HasMaxLength(49).IsFixedLength().IsRequired();
            b.Property(x => x.NumericCode).HasMaxLength(8).IsFixedLength().IsRequired();
            b.Property(x => x.IssuerRuc).HasMaxLength(13).IsFixedLength().IsRequired();
            b.Property(x => x.IssuerLegalName).HasMaxLength(300).IsRequired();
            b.Property(x => x.IssuerTradeName).HasMaxLength(300);
            b.Property(x => x.IssuerHeadOfficeAddress).HasMaxLength(300).IsRequired();
            b.Property(x => x.IssuerEstablishmentAddress).HasMaxLength(300).IsRequired();
            b.Property(x => x.TaxRegime).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.SpecialTaxpayerNumber).HasMaxLength(13);
            b.Property(x => x.WithholdingAgentNumber).HasMaxLength(13);
            b.Property(x => x.BuyerIdType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.BuyerIdNumber).HasMaxLength(20).IsRequired();
            b.Property(x => x.BuyerName).HasMaxLength(300).IsRequired();
            b.Property(x => x.BuyerAddress).HasMaxLength(300);
            b.Property(x => x.BuyerEmail).HasMaxLength(256);
            b.Property(x => x.BuyerPhone).HasMaxLength(40);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
            b.Property(x => x.SubtotalWithoutTaxes).HasPrecision(14, 2);
            b.Property(x => x.TotalDiscount).HasPrecision(14, 2);
            b.Property(x => x.TotalTax).HasPrecision(14, 2);
            b.Property(x => x.Total).HasPrecision(14, 2);
            b.Property(x => x.RoundingDifference).HasPrecision(14, 2);
            b.Ignore(x => x.Number);
            b.Property(x => x.AuthorizationNumber).HasMaxLength(49);
            b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.InvoiceId);
            b.HasMany(x => x.Taxes).WithOne().HasForeignKey(x => x.InvoiceId);
            b.HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.InvoiceId);
            b.HasMany<InvoiceEvent>().WithOne().HasForeignKey(e => e.InvoiceId);
            b.HasMany<InvoiceDocument>().WithOne().HasForeignKey(d => d.InvoiceId);
            b.HasMany<InvoiceSriAttempt>().WithOne().HasForeignKey(a => a.InvoiceId);
            b.HasOne<InvoiceTransmission>().WithOne().HasForeignKey<InvoiceTransmission>(t => t.InvoiceId);
            b.HasIndex(x => new { x.TenantId, x.SourceType, x.SourceId, x.DocumentType }).IsUnique();
            b.HasIndex(x => x.AccessKey).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.EmissionPointId, x.DocumentType, x.Environment, x.Sequential }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.IssueDate });
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<InvoiceLine>(b =>
        {
            b.ToTable("invoice_lines");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.MainCode).HasMaxLength(60).IsRequired();
            b.Property(x => x.Description).HasMaxLength(300).IsRequired();
            b.Property(x => x.Quantity).HasPrecision(14, 6);
            b.Property(x => x.UnitPrice).HasPrecision(14, 6);
            b.Property(x => x.Discount).HasPrecision(14, 2);
            b.Property(x => x.Subtotal).HasPrecision(14, 2);
            b.Property(x => x.TaxKind).HasMaxLength(20).IsRequired();
            b.Property(x => x.TaxCode).HasMaxLength(2).IsRequired();
            b.Property(x => x.TaxRateCode).HasMaxLength(4).IsRequired();
            b.Property(x => x.TaxPercent).HasPrecision(5, 2);
            b.Property(x => x.TaxAmount).HasPrecision(14, 2);
            b.HasIndex(x => new { x.InvoiceId, x.LineNumber }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<InvoiceTax>(b =>
        {
            b.ToTable("invoice_taxes");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.TaxCode).HasMaxLength(2).IsRequired();
            b.Property(x => x.RateCode).HasMaxLength(4).IsRequired();
            b.Property(x => x.Percent).HasPrecision(5, 2);
            b.Property(x => x.TaxableBase).HasPrecision(14, 2);
            b.Property(x => x.Amount).HasPrecision(14, 2);
            b.HasIndex(x => new { x.InvoiceId, x.TaxCode, x.RateCode }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<InvoicePayment>(b =>
        {
            b.ToTable("invoice_payments");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.MethodCode).HasMaxLength(2).IsRequired();
            b.Property(x => x.Method).HasMaxLength(20).IsRequired();
            b.Property(x => x.Amount).HasPrecision(14, 2);
            b.HasIndex(x => x.InvoiceId);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<InvoiceEvent>(b =>
        {
            b.ToTable("invoice_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasMaxLength(40).IsRequired();
            b.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Detail).HasMaxLength(500);
            b.HasIndex(x => new { x.InvoiceId, x.At });
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<InvoiceDocument>(b =>
        {
            b.ToTable("invoice_documents");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Content).HasColumnType("text").IsRequired();
            b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
            b.HasIndex(x => new { x.InvoiceId, x.Kind }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<InvoiceTransmission>(b =>
        {
            b.ToTable("invoice_transmissions");
            b.HasKey(x => x.InvoiceId);
            b.Property(x => x.InvoiceId).ValueGeneratedNever();
            b.Property(x => x.LastError).HasMaxLength(60);
            b.HasIndex(x => x.NextAttemptAt).HasFilter("NOT parked");
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<InvoiceSriAttempt>(b =>
        {
            b.ToTable("invoice_sri_attempts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Operation).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Environment).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Outcome).HasMaxLength(20).IsRequired();
            b.Property(x => x.SriState).HasMaxLength(60);
            b.Property(x => x.ErrorCode).HasMaxLength(60);
            b.Property(x => x.Messages).HasColumnType("jsonb");
            b.HasIndex(x => new { x.InvoiceId, x.At });
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<EmissionSequence>(b =>
        {
            b.ToTable("emission_sequences");
            b.HasKey(x => new { x.TenantId, x.EmissionPointId, x.DocumentType, x.Environment });
            b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Environment).HasConversion<string>().HasMaxLength(20);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<BranchRef>(b =>
        {
            b.ToTable("branches", "organizations", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        // La auditoría y la outbox pertenecen a Platform: aquí solo se mapean para que el interceptor compartido escriba en ellas dentro de la misma transacción.
        modelBuilder.Entity<AuditLog>(b =>
        {
            b.ToTable("audit_logs", "audit", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.Property(x => x.Before).HasColumnType("jsonb");
            b.Property(x => x.After).HasColumnType("jsonb");
            b.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages", "messaging", t => t.ExcludeFromMigrations());
            b.HasKey(x => x.Id);
            b.Property(x => x.Payload).HasColumnType("jsonb");
        });
    }
}

/// <summary>Sucursal (solo lectura): para mostrar el nombre de la sucursal de un punto de emisión y comprobar que existe.</summary>
internal sealed class BranchRef
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? SriEstablishmentCode { get; init; }
    public bool IsActive { get; init; }
}
