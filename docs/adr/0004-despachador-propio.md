# ADR 0004: Despachador propio en lugar de MediatR

**Estado:** Aprobado (Fase 0)

## Decisión
`IDispatcher` en `FitCore.Application.Core` resuelve `ICommandHandler<,>` e `IQueryHandler<,>` por inyección de dependencias. Los handlers devuelven `Result<T>`. Cada módulo registra sus handlers con `AddHandlersFromAssembly` al arrancar (un único recorrido de tipos; en ejecución solo hay resolución por DI). Los handlers son `internal`: la API solo conoce comandos, consultas y el despachador (lo verifica un test de arquitectura).

## Motivos
MediatR pasó a licencia comercial y nuestras necesidades son mínimas. Cuando hagan falta behaviors (validación, transacción, auditoría), se agregan como decoradores sobre el despachador.
