# ADR 0001: Monolito modular con Clean Architecture

**Estado:** Aprobado (Fase 0)

## Contexto
FIT360 tiene un dominio financiero que exige transacciones ACID entre módulos (pago, asignación, caja, asiento contable) y un equipo pequeño.

## Decisión
Un solo despliegue de API y un Worker, sobre una base PostgreSQL, divididos internamente en módulos con límites estrictos:
- Cada módulo con su esquema de BD y su `DbContext`.
- Comunicación entre módulos solo por `*.Contracts` y eventos de dominio (outbox transaccional).
- Sin FKs entre esquemas de módulos, salvo tenant y sucursal.
- Los límites se verifican con `FitCore.Architecture.Tests`.

## Consecuencias
Operación simple y consistencia fuerte. Un módulo se puede extraer como servicio más adelante porque sus contratos ya están aislados.
