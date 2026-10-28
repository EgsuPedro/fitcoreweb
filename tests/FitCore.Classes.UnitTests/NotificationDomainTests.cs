using FitCore.Classes.Domain;

namespace FitCore.Classes.UnitTests;

public class NotificationDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static InboxNotification Create(string channel = NotificationChannels.InApp, string title = "Reserva confirmada", string body = "Tienes cupo.") =>
        InboxNotification.Create(Guid.NewGuid(), Guid.NewGuid(), null, NotificationType.ReservationConfirmed, title, body, "{}", "reservation-confirmed:1", channel, Now);

    [Fact]
    public void Un_aviso_de_la_bandeja_nace_entregado_y_uno_externo_nace_pendiente()
    {
        var inApp = Create();
        var email = Create(NotificationChannels.Email);

        Assert.Equal(NotificationStatus.Delivered, inApp.Status);
        Assert.Equal(Now, inApp.DeliveredAt);
        Assert.Null(inApp.NextAttemptAt);
        Assert.Equal(NotificationStatus.Pending, email.Status);
        Assert.Null(email.DeliveredAt);
        Assert.Equal(Now, email.NextAttemptAt);
    }

    [Fact]
    public void Marcar_como_leido_conserva_la_primera_hora()
    {
        var notification = Create();

        notification.MarkRead(Now.AddMinutes(1));
        notification.MarkRead(Now.AddMinutes(9));

        Assert.True(notification.IsRead);
        Assert.Equal(Now.AddMinutes(1), notification.ReadAt);
    }

    [Fact]
    public void Un_fallo_transitorio_reintenta_con_espera_creciente()
    {
        var notification = Create(NotificationChannels.Email);

        notification.RecordFailure("timeout", transient: true, Now);
        Assert.Equal((NotificationStatus.Pending, 1, Now.AddMinutes(1)), (notification.Status, notification.Attempts, notification.NextAttemptAt!.Value));

        notification.RecordFailure("timeout", transient: true, Now);
        Assert.Equal(Now.AddMinutes(5), notification.NextAttemptAt);

        notification.RecordFailure("timeout", transient: true, Now);
        Assert.Equal(Now.AddMinutes(30), notification.NextAttemptAt);

        notification.RecordFailure("timeout", transient: true, Now);
        Assert.Equal(Now.AddHours(2), notification.NextAttemptAt);
        Assert.Equal("timeout", notification.LastError);
    }

    [Fact]
    public void Al_quinto_intento_fallido_el_aviso_deja_de_reintentarse()
    {
        var notification = Create(NotificationChannels.Sms);

        for (var i = 0; i < NotificationRetryPolicy.MaxAttempts; i++)
        {
            notification.RecordFailure("rate_limited", transient: true, Now);
        }

        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Null(notification.NextAttemptAt);
        Assert.Equal(NotificationRetryPolicy.MaxAttempts, notification.Attempts);
    }

    [Fact]
    public void Un_fallo_definitivo_no_se_reintenta()
    {
        var notification = Create(NotificationChannels.Push);

        notification.RecordFailure("invalid_recipient", transient: false, Now);

        Assert.Equal(NotificationStatus.Failed, notification.Status);
        Assert.Null(notification.NextAttemptAt);
        Assert.Equal(1, notification.Attempts);
    }

    [Fact]
    public void Entregar_tras_un_fallo_limpia_el_error_y_la_espera()
    {
        var notification = Create(NotificationChannels.Email);
        notification.RecordFailure("timeout", transient: true, Now);

        notification.MarkDelivered(Now.AddMinutes(2));

        Assert.Equal(NotificationStatus.Delivered, notification.Status);
        Assert.Equal(Now.AddMinutes(2), notification.DeliveredAt);
        Assert.Null(notification.LastError);
        Assert.Null(notification.NextAttemptAt);
    }

    [Fact]
    public void Los_textos_y_el_error_se_recortan_a_su_limite()
    {
        var notification = Create(NotificationChannels.Email, new string('t', 300), new string('b', 900));
        notification.RecordFailure(new string('e', 500), transient: true, Now);

        Assert.Equal(InboxNotification.MaxTitleLength, notification.Title.Length);
        Assert.Equal(InboxNotification.MaxBodyLength, notification.Body.Length);
        Assert.Equal(InboxNotification.MaxErrorLength, notification.LastError!.Length);
    }

    [Fact]
    public void El_catalogo_define_todos_los_tipos_y_marca_los_criticos()
    {
        Assert.Equal(Enum.GetValues<NotificationType>().Order(), NotificationCatalog.All.Select(i => i.Type).Order());
        Assert.Equal(
            [NotificationType.WaitlistOffer, NotificationType.ReservationCancelledByGym, NotificationType.SessionCancelled, NotificationType.SessionRescheduled],
            NotificationCatalog.All.Where(i => i.Critical).Select(i => i.Type));
        Assert.False(NotificationCatalog.IsCritical(NotificationType.ClassReminder));
    }

    [Fact]
    public void Las_reservas_y_las_sesiones_levantan_sus_eventos_para_la_outbox()
    {
        var reservation = ClassReservation.Confirm(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReservationSource.Staff, null, Now);
        var waiting = ClassReservation.Waitlist(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReservationSource.Member, null, Now);
        waiting.Offer(Now.AddMinutes(30), null, Now);
        waiting.Expire(null, Now.AddMinutes(31));
        reservation.Cancel("Cambio de planes", late: false, Guid.NewGuid(), Now, bySession: true, sessionReason: "Feriado");

        Assert.IsType<ReservationConfirmed>(Assert.Single(ClassReservation.Confirm(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReservationSource.Staff, null, Now).DomainEvents));
        Assert.Equal([typeof(WaitlistJoined), typeof(WaitlistOfferMade), typeof(WaitlistOfferExpired)], waiting.DomainEvents.Select(e => e.GetType()));
        var cancelled = Assert.IsType<ReservationCancelled>(reservation.DomainEvents.Last());
        Assert.True(cancelled.BySession);
        Assert.Equal("Feriado", cancelled.SessionReason);

        var starts = Now.AddDays(2);
        var session = ClassSession.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), starts, starts.AddHours(1), 10, null, null, null, Now);
        session.Reschedule(starts, starts.AddHours(1), session.InstructorId, 5, "Solo cambia la capacidad", null, null, Now);
        Assert.Empty(session.DomainEvents);
        session.Reschedule(starts.AddHours(2), starts.AddHours(3), session.InstructorId, 5, null, null, null, Now);
        session.Cancel("Feriado", null, Now);
        Assert.Equal([typeof(SessionRescheduled), typeof(SessionCancelled)], session.DomainEvents.Select(e => e.GetType()));
    }

    [Fact]
    public void Expirar_una_reserva_que_no_era_invitacion_no_avisa()
    {
        var waiting = ClassReservation.Waitlist(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ReservationSource.Member, null, Now);

        waiting.Expire(null, Now.AddHours(5));

        Assert.DoesNotContain(waiting.DomainEvents, e => e is WaitlistOfferExpired);
    }
}
