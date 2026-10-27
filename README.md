# FitCore 360

Plataforma SaaS para la administración integral de gimnasios (Ecuador, USD).

**Estado:** Fase 2A (backend de identidad y multiempresa). Implementado: gimnasios y sucursales, usuarios, roles y permisos, autenticación JWT con refresh rotativo, auditoría y outbox. Los módulos de negocio (alumnos, membresías, pagos…), Angular y Flutter aún no existen.

## Estructura

```
src/backend/
  FitCore.Api/                   Host HTTP: endpoints, JWT, autorización, ProblemDetails, OpenAPI
  FitCore.Worker/                Procesos en segundo plano (outbox)
  BuildingBlocks/
    FitCore.SharedKernel/        Result, Error, Money, Entity, IClock
    FitCore.Application.Core/    Despachador, contratos (ICurrentUser, ITenantContext), paginación
    FitCore.Infrastructure.Core/ Implementaciones comunes
  Modules/Platform/FitCore.Platform/
    Domain/                      Tenant, Branch, catálogo de permisos, plantillas de rol
    Identity/  Persistence/      Usuarios, roles, DbContext, RLS, migraciones, outbox
    Application/                 Casos de uso (handlers internos) y validadores
    Security/                    Tokens, contexto de acceso
tests/                           Unitarias, arquitectura e integración (PostgreSQL real)
infra/                           Dockerfiles y Docker Compose
docs/adr/                        Decisiones de arquitectura
```

## Requisitos
.NET SDK 9 y Docker Desktop (también lo necesitan las pruebas de integración).

## Comandos

```powershell
dotnet build FitCore.sln
dotnet test FitCore.sln          # ~1,5 min: levanta PostgreSQL con Testcontainers

# Entorno completo (API + Worker + PostgreSQL)
Copy-Item infra/compose/.env.example infra/compose/.env   # cambie TODOS los valores
docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env up --build
```

Con el entorno levantado:
- `http://localhost:8080/health/live` y `/health/ready` (comprueba PostgreSQL)
- `http://localhost:8080/openapi/v1.json` (solo en Development)
- Administrador de plataforma: el correo y la clave de `PLATFORM_ADMIN_*` en `infra/compose/.env`, con `tenantSlug: "platform"`.

### Primer recorrido

```powershell
$base = "http://localhost:8080/api/v1"
$j = @{ 'Content-Type' = 'application/json' }
# 1. Iniciar sesión como plataforma
$r = Invoke-RestMethod "$base/auth/login" -Method Post -ContentType 'application/json' `
  -Body (@{ tenantSlug='platform'; email='admin@fitcore.local'; password='<PLATFORM_ADMIN_PASSWORD>' } | ConvertTo-Json)
$h = @{ Authorization = "Bearer $($r.accessToken)" }
# 2. Crear un gimnasio con su propietario
Invoke-RestMethod "$base/platform/tenants" -Method Post -Headers $h -ContentType 'application/json' `
  -Body (@{ slug='mi-gym'; name='Mi Gym'; timeZone='America/Guayaquil'; locale='es-EC';
            ownerEmail='dueno@migym.com'; ownerFullName='Dueño'; ownerPassword='Clave-Segura-123' } | ConvertTo-Json)
```

## Endpoints (v1)

| Área | Rutas |
|---|---|
| Auth | `POST /auth/login`, `/auth/refresh`, `/auth/logout`, `/auth/change-password`; `GET /auth/me` |
| Plataforma | `GET/POST /platform/tenants`, `POST /platform/tenants/{id}/suspend` y `/activate` |
| Gimnasio | `GET/PUT /organization` |
| Sucursales | `GET/POST /branches`, `GET/PUT /branches/{id}` |
| Usuarios | `GET/POST /users`, `GET /users/{id}`, `PUT /users/{id}/access`, `PUT /users/{id}/status` |
| Roles y auditoría | `GET/POST /roles`, `PUT/DELETE /roles/{id}`, `GET /permissions`, `GET /audit-logs` |

Errores en `application/problem+json` con un `code` estable; validación con 422 y detalle por campo.

## Reglas
- El build trata las advertencias como errores.
- `FitCore.Architecture.Tests` falla si se rompen los límites entre capas.
- Un test falla si un endpoint no declara su autorización.
- Los secretos nunca van en el repositorio (`.env` está ignorado). Ver el ADR 0006 para el modelo de seguridad.
