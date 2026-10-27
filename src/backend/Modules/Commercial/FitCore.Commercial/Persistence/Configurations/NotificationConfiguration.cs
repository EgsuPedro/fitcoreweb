using FitCore.Commercial.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Commercial.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("notifications", CommercialDbContext.MembersSchema);
        b.HasKey(x => x.Id);
        b.Ignore(x => x.CanRetry);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.RecipientKind).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(10);
        b.Property(x => x.Recipient).HasMaxLength(20).IsRequired();
        b.Property(x => x.MessageText).HasMaxLength(1000).IsRequired();
        b.Property(x => x.LastError).HasMaxLength(500);
        b.Property(x => x.Provider).HasMaxLength(30);
        b.Property(x => x.ProviderMessageId).HasMaxLength(100);
        b.Property(x => x.ClaimedAt);

        b.HasOne<Member>().WithMany().HasForeignKey(x => new { x.TenantId, x.MemberId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Membership>().WithMany().HasForeignKey(x => new { x.TenantId, x.MembershipId })
            .HasPrincipalKey(x => new { x.TenantId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        // Cada aviso se genera una sola vez por membresía, tipo y destinatario, aunque el proceso corra muchas veces o en paralelo.
        b.HasIndex(x => new { x.TenantId, x.MembershipId, x.Type, x.Recipient }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
    }
}
