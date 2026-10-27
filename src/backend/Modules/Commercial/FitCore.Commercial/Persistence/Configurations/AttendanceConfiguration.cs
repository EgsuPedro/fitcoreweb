using FitCore.Commercial.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Commercial.Persistence.Configurations;

internal sealed class AttendanceEntryConfiguration : IEntityTypeConfiguration<AttendanceEntry>
{
    public void Configure(EntityTypeBuilder<AttendanceEntry> b)
    {
        b.ToTable("attendance", CommercialDbContext.AccessSchema);
        b.HasKey(x => x.Id);
        b.Ignore(x => x.IsOpen);
        b.Property(x => x.Method).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.CheckOutMethod).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.ExceptionReason).HasMaxLength(300);

        b.HasOne<Member>().WithMany().HasForeignKey(x => new { x.TenantId, x.MemberId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany().HasForeignKey(x => new { x.TenantId, x.MembershipId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.BranchId, x.CheckInAt });
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.CheckInAt });
    }
}
