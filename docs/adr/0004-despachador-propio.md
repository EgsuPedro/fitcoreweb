# ADR 0004: Despachador propio en lugar de MediatR

**Estado:** Aprobado (Fase 0)

## Decisión
`IDispatcher` en `FitCore.Application.Core` resuelve `ICommandHandler<,>` e `IQueryHandler<,>` por inyección de dependencias. Los handlers devuelven `Result<T>`. Los handlers se registran explícitamente en cada módulo.

## Motivos
MediatR pasó a licencia comercial y nuestras necesidades son mínimas. Cuando hagan falta behaviors (validación, transacción, auditoría), se agregan como decoradores sobre el despachador.
