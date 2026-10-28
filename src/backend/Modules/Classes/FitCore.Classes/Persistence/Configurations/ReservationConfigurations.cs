using FitCore.Classes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Classes.Persistence.Configurations;

internal sealed class ClassReservationConfiguration : IEntityTypeConfiguration<ClassReservation>
{
    public void Configure(EntityTypeBuilder<ClassReservation> b)
    {
        b.ToTable("class_reservations", ClassesDbContext.Schema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CancelReason).HasMaxLength(ClassReservation.MaxReasonLength);
        b.Ignore(x => x.IsLive);
        b.Ignore(x => x.HoldsSeat);
        b.Property<uint>("Version").IsRowVersion();

        b.HasIndex(x => new { x.TenantId, x.SessionId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.BranchId });
    }
}

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> b)
    {
        b.ToTable("idempotency_keys", ClassesDbContext.Schema);
        b.HasKey(x => new { x.TenantId, x.Key });
        b.Property(x => x.Key).HasMaxLength(100);
        b.Property(x => x.Scope).HasMaxLength(50);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.ResponseBody).HasColumnType("jsonb");
    }
}
