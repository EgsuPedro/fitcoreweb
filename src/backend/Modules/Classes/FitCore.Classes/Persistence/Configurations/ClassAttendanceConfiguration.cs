using FitCore.Classes.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FitCore.Classes.Persistence.Configurations;

internal sealed class ClassAttendanceConfiguration : IEntityTypeConfiguration<ClassAttendance>
{
    public void Configure(EntityTypeBuilder<ClassAttendance> b)
    {
        b.ToTable("class_attendance", ClassesDbContext.Schema);
        b.HasKey(x => x.Id);
        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(ClassAttendance.MaxNotesLength);
        b.Property<uint>("Version").IsRowVersion();

        // Una asistencia por reserva: registrar dos veces no duplica.
        b.HasIndex(x => new { x.TenantId, x.ReservationId }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.SessionId });
        b.HasIndex(x => new { x.TenantId, x.MemberId, x.CreatedAt });
    }
}
