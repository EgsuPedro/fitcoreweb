using FitCore.SharedKernel;

namespace FitCore.Analytics.Domain;

public enum PeriodPreset
{
    Today = 1,
    Last7Days = 2,
    Last30Days = 3,
    CurrentMonth = 4,
    PreviousMonth = 5,
    CurrentYear = 6,
    Custom = 7
}

/// <summary>
/// Un rango de fechas <b>locales</b> (ambas inclusive) en una zona horaria. Para columnas que guardan instantes se usa
/// <c>[StartUtc, EndUtcExclusive)</c>, el rango medio abierto que cubre exactamente esos días locales; las columnas de fecha local
/// (fecha de emisión, vencimiento, inicio de membresía…) se comparan directamente con <see cref="From"/> y <see cref="To"/>.
/// </summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To, string TimeZoneId)
{
    public int Days => To.DayNumber - From.DayNumber + 1;

    public DateTimeOffset StartUtc => LocalMidnightUtc(From, TimeZoneId);

    public DateTimeOffset EndUtcExclusive => LocalMidnightUtc(To.AddDays(1), TimeZoneId);

    /// <summary>Medianoche local de una fecha, como instante. Si esa hora no existe en la zona (salto de horario), se usa la siguiente hora válida.</summary>
    private static DateTimeOffset LocalMidnightUtc(DateOnly date, string timeZoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}

/// <summary>El período consultado y el comparable (misma duración, inmediatamente anterior o equivalente del ciclo anterior).</summary>
public sealed record PeriodPair(PeriodPreset Preset, ReportPeriod Current, ReportPeriod Previous);

public static class PeriodErrors
{
    public static readonly Error CustomRequiresDates = Error.Validation("analytics.period_required", "Un rango personalizado necesita fecha inicial y final.");
    public static readonly Error DatesWithPreset = Error.Validation("analytics.period_preset_with_dates", "Las fechas solo se envían con el rango personalizado.");
    public static readonly Error Inverted = Error.Validation("analytics.period_invalid", "La fecha inicial no puede ser posterior a la final.");
    public static readonly Error TooLong = Error.Validation("analytics.period_too_long", "El rango no puede superar 366 días.");
    public static readonly Error TooOld = Error.Validation("analytics.period_too_old", "La fecha inicial es demasiado antigua.");
}

public static class PeriodResolver
{
    public const int MaxDays = 366;

    public static readonly DateOnly Earliest = new(2000, 1, 1);

    /// <summary>
    /// Resuelve el rango pedido y su período comparable. <paramref name="today"/> es la fecha local de hoy en la zona de los datos
    /// (el servidor la calcula; el cliente nunca decide qué es «hoy»).
    /// </summary>
    public static Result<PeriodPair> Resolve(PeriodPreset preset, DateOnly? from, DateOnly? to, DateOnly today, string timeZoneId)
    {
        if (preset != PeriodPreset.Custom && (from is not null || to is not null))
        {
            return PeriodErrors.DatesWithPreset;
        }

        DateOnly start;
        DateOnly end;
        DateOnly prevStart;
        DateOnly prevEnd;
        switch (preset)
        {
            case PeriodPreset.Today:
                (start, end) = (today, today);
                (prevStart, prevEnd) = (today.AddDays(-1), today.AddDays(-1));
                break;
            case PeriodPreset.Last7Days:
                (start, end) = (today.AddDays(-6), today);
                (prevStart, prevEnd) = (today.AddDays(-13), today.AddDays(-7));
                break;
            case PeriodPreset.Last30Days:
                (start, end) = (today.AddDays(-29), today);
                (prevStart, prevEnd) = (today.AddDays(-59), today.AddDays(-30));
                break;
            case PeriodPreset.CurrentMonth:
            {
                (start, end) = (new DateOnly(today.Year, today.Month, 1), today);
                var previousMonth = start.AddMonths(-1);
                var length = Math.Min(end.DayNumber - start.DayNumber + 1, DateTime.DaysInMonth(previousMonth.Year, previousMonth.Month));
                (prevStart, prevEnd) = (previousMonth, previousMonth.AddDays(length - 1));
                break;
            }

            case PeriodPreset.PreviousMonth:
            {
                var thisMonth = new DateOnly(today.Year, today.Month, 1);
                start = thisMonth.AddMonths(-1);
                end = thisMonth.AddDays(-1);
                prevStart = start.AddMonths(-1);
                prevEnd = start.AddDays(-1);
                break;
            }

            case PeriodPreset.CurrentYear:
            {
                (start, end) = (new DateOnly(today.Year, 1, 1), today);
                var previousYear = start.AddYears(-1);
                var length = Math.Min(end.DayNumber - start.DayNumber + 1, DateTime.IsLeapYear(previousYear.Year) ? 366 : 365);
                (prevStart, prevEnd) = (previousYear, previousYear.AddDays(length - 1));
                break;
            }

            case PeriodPreset.Custom:
            {
                if (from is not { } f || to is not { } t)
                {
                    return PeriodErrors.CustomRequiresDates;
                }

                if (f > t)
                {
                    return PeriodErrors.Inverted;
                }

                if (f < Earliest)
                {
                    return PeriodErrors.TooOld;
                }

                (start, end) = (f, t);
                var days = end.DayNumber - start.DayNumber + 1;
                if (days > MaxDays)
                {
                    return PeriodErrors.TooLong;
                }

                (prevStart, prevEnd) = (start.AddDays(-days), start.AddDays(-1));
                break;
            }

            default:
                return PeriodErrors.CustomRequiresDates;
        }

        return new PeriodPair(preset, new ReportPeriod(start, end, timeZoneId), new ReportPeriod(prevStart, prevEnd, timeZoneId));
    }
}
