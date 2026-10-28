using FitCore.Classes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Classes.Persistence.Configurations;

internal sealed class InboxNotificationConfiguration : IEntityTypeConfiguration<InboxNotification>
{
    public void Configure(EntityTypeBuilder<InboxNotification> b)
    {
        b.ToTable("inbox_notifications", ClassesDbContext.Schema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.Title).HasMaxLength(InboxNotification.MaxTitleLength).IsRequired();
        b.Property(x => x.Body).HasMaxLength(InboxNotification.MaxBodyLength).IsRequired();
        b.Property(x => x.Data).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.DedupeKey).HasMaxLength(InboxNotification.MaxDedupeKeyLength).IsRequired();
        b.Property(x => x.Channel).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.LastError).HasMaxLength(InboxNotification.MaxErrorLength);
        b.Ignore(x => x.IsRead);
        b.Property<uint>("Version").IsRowVersion();

        // El mismo hecho nunca avisa dos veces al mismo usuario (lo decide la base si dos procesos lo intentan a la vez).
        b.HasIndex(x => new { x.TenantId, x.RecipientUserId, x.DedupeKey }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.RecipientUserId, x.CreatedAt });
    }
}

internal sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.ToTable("notification_preferences", ClassesDbContext.Schema);
        b.HasKey(x => new { x.TenantId, x.UserId, x.Type });
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
    }
}
