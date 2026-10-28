using FitCore.Classes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Classes.Persistence.Configurations;

internal sealed class ClassSeriesConfiguration : IEntityTypeConfiguration<ClassSeries>
{
    public void Configure(EntityTypeBuilder<ClassSeries> b)
    {
        b.ToTable("class_series", ClassesDbContext.Schema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.CancelReason).HasMaxLength(ClassSession.MaxCancelReasonLength);
        b.Ignore(x => x.Days);
        b.Ignore(x => x.IsActive);
        b.Property<uint>("Version").IsRowVersion();

        b.HasIndex(x => new { x.TenantId, x.ClassId });
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.Status });
    }
}

internal sealed class ClassSessionConfiguration : IEntityTypeConfiguration<ClassSession>
{
    public void Configure(EntityTypeBuilder<ClassSession> b)
    {
        b.ToTable("class_sessions", ClassesDbContext.Schema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(ClassSession.MaxNotesLength);
        b.Property(x => x.CancelReason).HasMaxLength(ClassSession.MaxCancelReasonLength);
        b.Property(x => x.AvailabilityOverrideReason).HasMaxLength(ClassSession.MaxOverrideReasonLength);
        b.Ignore(x => x.IsScheduled);
        b.Ignore(x => x.IsModified);
        b.Property<uint>("Version").IsRowVersion();

        // El calendario consulta por sucursal y rango de fechas; el instructor, por su propia agenda.
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.StartsAt });
        b.HasIndex(x => new { x.TenantId, x.InstructorId, x.StartsAt });
        b.HasIndex(x => new { x.TenantId, x.ClassId, x.StartsAt });
        b.HasIndex(x => new { x.TenantId, x.SeriesId });
    }
}
