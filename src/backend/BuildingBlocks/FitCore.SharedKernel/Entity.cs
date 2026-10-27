namespace FitCore.SharedKernel;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
}

public abstract record DomainEvent(Guid EventId, DateTimeOffset OccurredAt) : IDomainEvent;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Entidad de negocio que pertenece a un tenant (aislamiento multiempresa).</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

public abstract class Entity<TId>
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected Entity(TId id) => Id = id;

    public TId Id { get; }

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}
