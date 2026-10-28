using FitCore.Wellness.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Wellness.Persistence.Configurations;

internal sealed class PhotoConsentConfiguration : IEntityTypeConfiguration<PhotoConsent>
{
    public void Configure(EntityTypeBuilder<PhotoConsent> b)
    {
        b.ToTable("photo_consents", WellnessDbContext.WellnessSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.GuardianName).HasMaxLength(PhotoConsent.MaxGuardianNameLength);
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.RecordedAt });
    }
}

internal sealed class ProgressPhotoConfiguration : IEntityTypeConfiguration<ProgressPhoto>
{
    public void Configure(EntityTypeBuilder<ProgressPhoto> b)
    {
        b.ToTable("progress_photos", WellnessDbContext.WellnessSchema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Angle).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.StorageKey).HasMaxLength(ProgressPhoto.MaxStorageKeyLength).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.StorageKey).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.TakenOn });
    }
}
