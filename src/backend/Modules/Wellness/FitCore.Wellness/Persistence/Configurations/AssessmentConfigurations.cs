using FitCore.Wellness.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Wellness.Persistence.Configurations;

internal sealed class BodyAssessmentConfiguration : IEntityTypeConfiguration<BodyAssessment>
{
    public void Configure(EntityTypeBuilder<BodyAssessment> b)
    {
        b.ToTable("body_assessments", WellnessDbContext.WellnessSchema);
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.TenantId, x.Id });
        b.Property(x => x.WeightKg).HasPrecision(5, 1);
        b.Property(x => x.HeightCm).HasPrecision(4, 1);
        b.Property(x => x.WaistCm).HasPrecision(4, 1);
        b.Property(x => x.HipCm).HasPrecision(4, 1);
        b.Property(x => x.ChestCm).HasPrecision(4, 1);
        b.Property(x => x.ArmCm).HasPrecision(4, 1);
        b.Property(x => x.ThighCm).HasPrecision(4, 1);
        b.Property(x => x.BodyFatPercent).HasPrecision(4, 1);
        b.Property(x => x.BodyFatMethod).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(BodyAssessment.MaxNotesLength);
        b.HasOne<BodyAssessment>().WithMany().HasForeignKey(x => new { x.TenantId, x.CorrectsId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        // Una evaluación solo puede ser corregida una vez (la corrección tiene su propia fila).
        b.HasIndex(x => x.CorrectsId).IsUnique().HasFilter("corrects_id IS NOT NULL").HasDatabaseName("ux_body_assessments_corrects");
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.AssessedOn });
    }
}
