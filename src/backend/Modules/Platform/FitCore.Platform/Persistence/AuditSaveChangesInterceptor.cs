using System.Text.Json;
using FitCore.Application.Core;
using FitCore.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace FitCore.Platform.Persistence;

public sealed class TenantViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// En cada SaveChanges: (1) impide escribir filas de otro tenant, (2) registra la auditoría de las
/// entidades <see cref="IAuditable"/> y (3) mueve los eventos de dominio a la outbox, todo en la misma transacción.
/// </summary>
public sealed class AuditSaveChangesInterceptor(IServiceProvider services, ITenantContext tenantContext, IClock clock)
    : SaveChangesInterceptor
{
    private static readonly HashSet<string> NoisyProperties =
        ["ConcurrencyStamp", "AccessFailedCount", "LockoutEnd", "NormalizedEmail", "NormalizedUserName", "NormalizedName", "Version"];

    private static readonly HashSet<string> SecretProperties = ["PasswordHash", "SecurityStamp", "QrTokenHash", "EncryptedP12", "EncryptedPassword"];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Process(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Process(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Process(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var entries = context.ChangeTracker.Entries().ToList();

        GuardTenant(entries);
        AddAuditLogs(context, entries);
        MoveDomainEventsToOutbox(context, entries);
    }

    private void GuardTenant(List<EntityEntry> entries)
    {
        foreach (var entry in entries.Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is not ITenantOwned owned)
            {
                continue;
            }

            if (tenantContext.TenantId is not { } current || owned.TenantId != current)
            {
                throw new TenantViolationException(
                    $"Intento de escribir {entry.Metadata.ClrType.Name} del tenant {owned.TenantId} con el tenant activo {tenantContext.TenantId}.");
            }

            if (entry.State == EntityState.Modified && entry.Property(nameof(ITenantOwned.TenantId)).IsModified)
            {
                throw new TenantViolationException("El tenant de una fila no puede modificarse.");
            }
        }
    }

    private void AddAuditLogs(DbContext context, List<EntityEntry> entries)
    {
        // Sin tenant activo (p. ej. un job) no hay a quién atribuir la auditoría.
        if (tenantContext.TenantId is not { } tenantId)
        {
            return;
        }

        var currentUser = services.GetService<ICurrentUser>();
        var request = services.GetService<IRequestInfo>();

        foreach (var entry in entries.Where(e => e.Entity is IAuditable && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            var (before, after) = SnapshotChanges(entry);
            if (entry.State == EntityState.Modified && after.Count == 0)
            {
                continue;
            }

            var key = entry.Metadata.FindPrimaryKey()!.Properties
                .Select(p => entry.Property(p.Name).CurrentValue?.ToString())
                .ToList();
            var verb = entry.State switch
            {
                EntityState.Added => "Created",
                EntityState.Deleted => "Deleted",
                _ => "Updated"
            };

            context.Set<AuditLog>().Add(new AuditLog
            {
                TenantId = tenantId,
                UserId = currentUser?.UserId,
                Action = $"{entry.Metadata.ClrType.Name}.{verb}",
                Entity = entry.Metadata.ClrType.Name,
                EntityId = string.Join('|', key),
                Before = before.Count == 0 ? null : JsonSerializer.Serialize(before),
                After = after.Count == 0 ? null : JsonSerializer.Serialize(after),
                IpAddress = request?.IpAddress,
                CorrelationId = request?.CorrelationId,
                OccurredAt = clock.UtcNow
            });
        }
    }

    private static (Dictionary<string, object?> Before, Dictionary<string, object?> After) SnapshotChanges(EntityEntry entry)
    {
        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();

        foreach (var property in entry.Properties.Where(p => !p.Metadata.IsShadowProperty() || p.Metadata.Name != "Version"))
        {
            var name = property.Metadata.Name;
            if (NoisyProperties.Contains(name))
            {
                continue;
            }

            var secret = SecretProperties.Contains(name);
            switch (entry.State)
            {
                case EntityState.Added:
                    after[name] = secret ? "[redacted]" : property.CurrentValue;
                    break;
                case EntityState.Deleted:
                    before[name] = secret ? "[redacted]" : property.OriginalValue;
                    break;
                case EntityState.Modified when property.IsModified:
                    before[name] = secret ? "[redacted]" : property.OriginalValue;
                    after[name] = secret ? "[redacted]" : property.CurrentValue;
                    break;
                default:
                    break;
            }
        }

        return (before, after);
    }

    private void MoveDomainEventsToOutbox(DbContext context, List<EntityEntry> entries)
    {
        foreach (var entry in entries.Where(e => e.Entity is IHasDomainEvents))
        {
            var holder = (IHasDomainEvents)entry.Entity;
            foreach (var domainEvent in holder.DomainEvents)
            {
                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    Id = domainEvent.EventId,
                    TenantId = tenantContext.TenantId,
                    Type = domainEvent.GetType().FullName ?? domainEvent.GetType().Name,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType()),
                    OccurredAt = domainEvent.OccurredAt
                });
            }

            holder.ClearDomainEvents();
        }
    }
}
