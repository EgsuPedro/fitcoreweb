using FitCore.Commercial.Domain;

namespace FitCore.Commercial.UnitTests;

public class NotificationDomainTests
{
    [Theory]
    [InlineData("0991234567", "+593991234567")]
    [InlineData("099 123 4567", "+593991234567")]
    [InlineData("099-123-4567", "+593991234567")]
    [InlineData("+593991234567", "+593991234567")]
    [InlineData("+593 99 123 4567", "+593991234567")]
    [InlineData("593991234567", "+593991234567")]
    [InlineData("00593991234567", "+593991234567")]
    [InlineData("+1 (305) 555-0100", "+13055550100")]
    [InlineData("022345678", "+59322345678")]
    public void Normaliza_telefonos_validos_al_formato_internacional(string raw, string expected) =>
        Assert.Equal(expected, PhoneNumbers.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("991234567")]
    [InlineData("0991234567890123")]
    [InlineData("+59")]
    public void Rechaza_lo_que_no_parece_un_telefono(string? raw) => Assert.Null(PhoneNumbers.Normalize(raw));

    private static Notification NewNotification() =>
        Notification.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NotificationType.StaffExpiringSoon,
            NotificationRecipientKind.Staff, "+593991234567", "texto", new DateOnly(2026, 11, 1), DateTimeOffset.UtcNow);

    [Fact]
    public void Una_notificacion_nueva_esta_pendiente_sin_intentos()
    {
        var n = NewNotification();

        Assert.Equal(NotificationStatus.Pending, n.Status);
        Assert.Equal(0, n.Attempts);
        Assert.True(n.CanRetry);
    }

    [Fact]
    public void Marcar_enviada_registra_proveedor_id_y_fecha_y_limpia_el_error()
    {
        var n = NewNotification();
        n.MarkFailed("simulated", "falló");
        var now = DateTimeOffset.UtcNow;

        n.MarkSent("simulated", "sim-1", now);

        Assert.Equal(NotificationStatus.Sent, n.Status);
        Assert.Equal("sim-1", n.ProviderMessageId);
        Assert.Equal(now, n.SentAt);
        Assert.Null(n.LastError);
        Assert.False(n.CanRetry);
    }

    [Fact]
    public void Un_error_largo_se_recorta()
    {
        var n = NewNotification();

        n.MarkFailed("meta", new string('x', 2000));

        Assert.Equal(NotificationStatus.Failed, n.Status);
        Assert.Equal(500, n.LastError!.Length);
    }

    [Fact]
    public void La_configuracion_guarda_los_telefonos_del_personal_y_empieza_apagada()
    {
        var settings = CommercialSettings.CreateDefault(Guid.NewGuid());
        Assert.False(settings.NotificationsEnabled);
        Assert.Empty(settings.StaffPhones);

        settings.UpdateNotifications(true, ["+593991111111", "+593992222222"], Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.True(settings.NotificationsEnabled);
        Assert.Equal(["+593991111111", "+593992222222"], settings.StaffPhones);
    }
}
