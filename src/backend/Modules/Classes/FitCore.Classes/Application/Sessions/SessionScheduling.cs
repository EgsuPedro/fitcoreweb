using FitCore.Classes.Domain;
using FitCore.Classes.Persistence;
using FitCore.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FitCore.Classes.Application.Sessions;

/// <summary>Consultas y reglas de horario compartidas por los casos de uso de sesiones y series.</summary>
internal static class SessionScheduling
{
    /// <summary>Sesiones de las sucursales a las que el usuario tiene acceso (las demás no existen para él).</summary>
    public static IQueryable<ClassSession> VisibleTo(this IQueryable<ClassSession> sessions, UserAccess actor)
    {
        if (actor.AllBranches)
        {
            return sessions;
        }

        var branchIds = actor.BranchIds.ToList();
        return sessions.Where(s => branchIds.Contains(s.BranchId));
    }

    public static IQueryable<ClassSeries> VisibleTo(this IQueryable<ClassSeries> series, UserAccess actor)
    {
        if (actor.AllBranches)
        {
            return series;
        }

        var branchIds = actor.BranchIds.ToList();
        return series.Where(s => branchIds.Contains(s.BranchId));
    }

    /// <summary>Violación de la restricción de no solapamiento del instructor o de unicidad de la serie: otra solicitud llegó primero.</summary>
    public static bool IsConcurrencyConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation };

    /// <summary>
    /// Además de lo anterior, que la fila cambiara entre la lectura y la escritura (token de versión) o que PostgreSQL abortara esta
    /// transacción por un interbloqueo con otra solicitud igual: en todos los casos vale la pena releer y reintentar.
    /// </summary>
    public static bool IsRetryable(DbUpdateException exception) =>
        exception is DbUpdateConcurrencyException
        || IsConcurrencyConflict(exception)
        || exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure };

    public static async Task<TimeZoneInfo> ZoneAsync(ClassesDbContext db, Guid branchId, CancellationToken cancellationToken)
    {
        var id = await db.BranchRefs.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.TimeZone).FirstOrDefaultAsync(cancellationToken);
        return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(id) ? FitCore.Platform.Domain.Tenant.DefaultTimeZone : id);
    }

    public static async Task<IReadOnlyList<InstructorSlot>> SlotsAsync(ClassesDbContext db, Guid instructorId, CancellationToken cancellationToken) =>
        [.. (await db.TrainerAvailabilityRefs.AsNoTracking().Where(a => a.TrainerId == instructorId).ToListAsync(cancellationToken))
            .Select(a => new InstructorSlot((DayOfWeek)a.Day, a.StartTime, a.EndTime, a.BranchId))];

    /// <summary>¿El instructor ya tiene otra sesión no cancelada que se cruza con el rango? (el fin de una no cruza con el inicio de la siguiente)</summary>
    public static Task<bool> OverlapsAsync(ClassesDbContext db, Guid instructorId, DateTimeOffset startsAt, DateTimeOffset endsAt, Guid exceptSessionId, CancellationToken cancellationToken) =>
        db.ClassSessions.AsNoTracking().AnyAsync(
            s => s.InstructorId == instructorId && s.Id != exceptSessionId && s.Status != SessionStatus.Cancelled && s.StartsAt < endsAt && s.EndsAt > startsAt,
            cancellationToken);

    public static async Task<Dictionary<Guid, (string Name, string Activity)>> ClassNamesAsync(ClassesDbContext db, IReadOnlyCollection<Guid> classIds, CancellationToken cancellationToken) =>
        (await db.GroupClasses.AsNoTracking().Where(c => classIds.Contains(c.Id)).Select(c => new { c.Id, c.Name, c.Activity }).ToListAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => (c.Name, c.Activity.ToString()));

    public static async Task<Dictionary<Guid, string>> InstructorNamesAsync(ClassesDbContext db, IReadOnlyCollection<Guid> trainerIds, CancellationToken cancellationToken) =>
        await db.TrainerRefs.AsNoTracking()
            .Where(t => trainerIds.Contains(t.Id))
            .Join(db.UserRefs.AsNoTracking(), t => t.UserId, u => u.Id, (t, u) => new { t.Id, u.FullName })
            .ToDictionaryAsync(x => x.Id, x => x.FullName, cancellationToken);

    public static async Task<Dictionary<Guid, string>> BranchNamesAsync(ClassesDbContext db, IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken) =>
        await db.BranchRefs.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

    public static async Task<IReadOnlyList<ClassSessionDto>> ToDtosAsync(ClassesDbContext db, IReadOnlyList<ClassSession> sessions, CancellationToken cancellationToken)
    {
        var classes = await ClassNamesAsync(db, [.. sessions.Select(s => s.ClassId).Distinct()], cancellationToken);
        var instructors = await InstructorNamesAsync(db, [.. sessions.Select(s => s.InstructorId).Distinct()], cancellationToken);
        var branches = await BranchNamesAsync(db, [.. sessions.Select(s => s.BranchId).Distinct()], cancellationToken);
        var counts = await ReservationCountsAsync(db, [.. sessions.Select(s => s.Id)], cancellationToken);

        return
        [
            .. sessions.Select(s =>
            {
                var known = classes.TryGetValue(s.ClassId, out var c);
                return ClassSessionDto.From(s, known ? c.Name : string.Empty, known ? c.Activity : string.Empty, instructors.GetValueOrDefault(s.InstructorId, string.Empty), branches.GetValueOrDefault(s.BranchId, string.Empty), counts.GetValueOrDefault(s.Id).Booked, counts.GetValueOrDefault(s.Id).Waitlist);
            })
        ];
    }

    /// <summary>Cupos ocupados (confirmadas y ofrecidas) y personas en espera de cada sesión.</summary>
    public static async Task<Dictionary<Guid, (int Booked, int Waitlist)>> ReservationCountsAsync(ClassesDbContext db, IReadOnlyCollection<Guid> sessionIds, CancellationToken cancellationToken) =>
        (await db.Reservations.AsNoTracking()
            .Where(r => sessionIds.Contains(r.SessionId))
            .GroupBy(r => r.SessionId)
            .Select(g => new
            {
                g.Key,
                Booked = g.Count(x => x.Status == ReservationStatus.Confirmed || x.Status == ReservationStatus.Offered),
                Waitlist = g.Count(x => x.Status == ReservationStatus.Waitlisted)
            })
            .ToListAsync(cancellationToken))
        .ToDictionary(x => x.Key, x => (x.Booked, x.Waitlist));

    /// <summary>Bloquea las filas de las sesiones, en orden de id para que dos operaciones cruzadas no se interbloqueen. Quien reserva espera aquí.</summary>
    public static async Task LockSessionsAsync(ClassesDbContext db, IEnumerable<Guid> sessionIds, CancellationToken cancellationToken)
    {
        var ids = sessionIds.Distinct().Order().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM classes.class_sessions WHERE id = ANY({ids}) ORDER BY id FOR UPDATE", cancellationToken);
    }

    /// <summary>Cancela las reservas vivas de sesiones que se cancelan (el cupo ya no existe). Devuelve cuántas.</summary>
    public static async Task<int> CancelLiveReservationsAsync(ClassesDbContext db, IReadOnlyCollection<Guid> sessionIds, string reason, Guid? by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var live = await db.Reservations
            .Where(r => sessionIds.Contains(r.SessionId) && (r.Status == ReservationStatus.Confirmed || r.Status == ReservationStatus.Waitlisted || r.Status == ReservationStatus.Offered))
            .ToListAsync(cancellationToken);
        var text = "Sesión cancelada: " + reason.Trim();
        foreach (var reservation in live)
        {
            reservation.Cancel(text.Length > ClassReservation.MaxReasonLength ? text[..ClassReservation.MaxReasonLength] : text, late: false, by, now);
        }

        return live.Count;
    }

    public static async Task<ClassSessionDto> ToDtoAsync(ClassesDbContext db, ClassSession session, CancellationToken cancellationToken) =>
        (await ToDtosAsync(db, [session], cancellationToken))[0];

    public static async Task<IReadOnlyList<ClassSeriesDto>> ToSeriesDtosAsync(ClassesDbContext db, IReadOnlyList<ClassSeries> series, CancellationToken cancellationToken)
    {
        var ids = series.Select(s => s.Id).ToList();
        var counts = (await db.ClassSessions.AsNoTracking()
                .Where(s => s.SeriesId != null && ids.Contains(s.SeriesId.Value))
                .GroupBy(s => s.SeriesId)
                .Select(g => new { g.Key, Total = g.Count(), Scheduled = g.Count(x => x.Status == SessionStatus.Scheduled) })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Key!.Value, x => (x.Total, x.Scheduled));
        var classes = await ClassNamesAsync(db, [.. series.Select(s => s.ClassId).Distinct()], cancellationToken);
        var instructors = await InstructorNamesAsync(db, [.. series.Select(s => s.InstructorId).Distinct()], cancellationToken);
        var branches = await BranchNamesAsync(db, [.. series.Select(s => s.BranchId).Distinct()], cancellationToken);

        return
        [
            .. series.Select(s =>
            {
                counts.TryGetValue(s.Id, out var count);
                var (total, scheduled) = count;
                return ClassSeriesDto.From(s, classes.TryGetValue(s.ClassId, out var c) ? c.Name : string.Empty, instructors.GetValueOrDefault(s.InstructorId, string.Empty), branches.GetValueOrDefault(s.BranchId, string.Empty), total, scheduled);
            })
        ];
    }

    public static async Task<ClassSeriesDto> ToSeriesDtoAsync(ClassesDbContext db, ClassSeries series, CancellationToken cancellationToken) =>
        (await ToSeriesDtosAsync(db, [series], cancellationToken))[0];

    /// <summary>
    /// Qué sesiones generaría la serie ahora. Es idempotente: las fechas que ya tienen su sesión (aunque se haya movido o cancelado) no se
    /// vuelven a crear, y las que chocan con el instructor se informan con su motivo en lugar de omitirse en silencio.
    /// </summary>
    public static async Task<SeriesPlan> PlanAsync(ClassesDbContext db, ClassSeries series, TimeZoneInfo zone, Guid? by, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var skipped = new List<SkippedOccurrenceDto>();
        var candidates = new List<(DateOnly Date, DateTimeOffset Start)>();
        var past = 0;
        foreach (var date in series.Dates(series.EndDate))
        {
            if (ClassSeries.ToUtc(date, series.StartTime, zone) is not { } start)
            {
                skipped.Add(new SkippedOccurrenceDto(date, "invalid_time"));
                continue;
            }

            if (start <= now)
            {
                past++;
                continue;
            }

            candidates.Add((date, start));
        }

        if (candidates.Count == 0)
        {
            return new SeriesPlan([], 0, past, skipped);
        }

        var duration = TimeSpan.FromMinutes(series.DurationMinutes);
        var rangeStart = candidates.Min(c => c.Start);
        var rangeEnd = candidates.Max(c => c.Start) + duration;
        var existing = (await db.ClassSessions.AsNoTracking().Where(s => s.SeriesId == series.Id).Select(s => s.OriginalStartsAt).ToListAsync(cancellationToken)).ToHashSet();
        var busy = (await db.ClassSessions.AsNoTracking()
                .Where(s => s.InstructorId == series.InstructorId && s.Status != SessionStatus.Cancelled && s.StartsAt < rangeEnd && s.EndsAt > rangeStart)
                .Select(s => new { s.StartsAt, s.EndsAt })
                .ToListAsync(cancellationToken))
            .Select(s => (Start: s.StartsAt, End: s.EndsAt))
            .ToList();
        var slots = await SlotsAsync(db, series.InstructorId, cancellationToken);

        var created = new List<ClassSession>();
        var alreadyExisted = 0;
        foreach (var (date, start) in candidates)
        {
            if (existing.Contains(start))
            {
                alreadyExisted++;
                continue;
            }

            var end = start + duration;
            if (busy.Any(b => b.Start < end && b.End > start))
            {
                skipped.Add(new SkippedOccurrenceDto(date, "instructor_overlap"));
                continue;
            }

            if (!InstructorAvailabilityRules.Fits(slots, series.BranchId, start, end, zone))
            {
                skipped.Add(new SkippedOccurrenceDto(date, "instructor_unavailable"));
                continue;
            }

            created.Add(ClassSession.Create(series.TenantId, series.BranchId, series.ClassId, series.Id, series.InstructorId, start, end, series.Capacity, null, null, by, now));
            busy.Add((start, end));
        }

        return new SeriesPlan(created, alreadyExisted, past, skipped);
    }
}

internal sealed record SeriesPlan(List<ClassSession> Created, int AlreadyExisted, int Past, List<SkippedOccurrenceDto> Skipped);
