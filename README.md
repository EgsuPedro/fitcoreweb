# FitCore 360

Plataforma SaaS para la administración integral de gimnasios (Ecuador, USD).

**Estado:** Fase 1 (fundamentos). Solo existe la infraestructura base del backend. Los módulos de negocio, Angular y Flutter aún no están implementados.

## Estructura

```
src/backend/
  FitCore.Api/                 Host HTTP (ProblemDetails, OpenAPI, health, localización)
  FitCore.Worker/              Host de procesos en segundo plano
  BuildingBlocks/
    FitCore.SharedKernel/      Result, Error, Money, Entity, IClock
    FitCore.Application.Core/  Despachador de comandos y consultas, ICurrentUser
    FitCore.Infrastructure.Core/  Implementaciones comunes (reloj)
tests/                         Unitarias, arquitectura e integración
infra/                         Dockerfiles y Docker Compose
docs/adr/                      Decisiones de arquitectura
```

## Requisitos
.NET SDK 9 y Docker Desktop.

## Comandos

```powershell
dotnet build FitCore.sln
dotnet test FitCore.sln

# Entorno completo (API + Worker + PostgreSQL)
Copy-Item infra/compose/.env.example infra/compose/.env   # cambie la contraseña
docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env up --build
```

Con el entorno levantado:
- `http://localhost:8080/health/live` y `/health/ready` (este último comprueba PostgreSQL)
- `http://localhost:8080/api/v1/system/info`
- `http://localhost:8080/openapi/v1.json` (solo en Development)

## Reglas
- El build trata las advertencias como errores.
- `FitCore.Architecture.Tests` falla si se rompen los límites entre capas.
- Los secretos nunca van en el repositorio (`.env` está ignorado).
