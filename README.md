# FitCore 360

Plataforma SaaS para la administración integral de gimnasios (Ecuador, USD).

**Estado:** Fase 2 completa (identidad, multiempresa y shell web). Implementado: gimnasios, sucursales, usuarios, roles y permisos con edición, autenticación JWT con refresh rotativo, auditoría, outbox y un frontend Angular (login, menú por permisos, sucursales, usuarios, roles, auditoría y administración de gimnasios). Los módulos de negocio (alumnos, membresías, pagos…) y Flutter aún no existen.

## Estructura

```
src/backend/
  FitCore.Api/                   Host HTTP: endpoints, JWT, autorización, ProblemDetails, OpenAPI
  FitCore.Worker/                Procesos en segundo plano (outbox)
  BuildingBlocks/                SharedKernel, Application.Core (despachador, contratos), Infrastructure.Core
  Modules/Platform/FitCore.Platform/
    Domain/                      Tenant, Branch, catálogo de permisos, plantillas de rol
    Identity/  Persistence/      Usuarios, roles, DbContext, RLS, migraciones, outbox
    Application/                 Casos de uso (handlers internos) y validadores
    Security/                    Tokens, contexto de acceso
src/web/                         Angular 22 (standalone, señales, Transloco es/en)
  src/app/core/                  Sesión, interceptor, guards, cliente de la API, i18n
  src/app/layout/  features/     Shell con menú por permisos; pantallas por funcionalidad
  public/i18n/                   Traducciones (es por defecto, en)
tests/
  FitCore.*.Tests/               Unitarias, arquitectura e integración (PostgreSQL real)
  e2e/                           Playwright: navegador real contra la API y la base de datos
infra/                           Dockerfiles (API, Worker, Web+nginx) y Docker Compose
docs/adr/                        Decisiones de arquitectura
```

## Requisitos
.NET SDK 9, Node 24 y Docker Desktop (también lo necesitan las pruebas de integración).

## Comandos

```powershell
# Backend
dotnet build FitCore.sln
dotnet test FitCore.sln                       # ~1,5 min: levanta PostgreSQL con Testcontainers

# Entorno completo (PostgreSQL + API + Worker + Web)
Copy-Item infra/compose/.env.example infra/compose/.env   # cambie TODOS los valores
docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env up --build

# Frontend
cd src/web
npm ci
npm start                                     # http://localhost:4300 (proxy a la API en :8080)
npm test                                      # pruebas unitarias (Vitest)
npm run generate:api                          # regenera los tipos desde el OpenAPI de la API

# End-to-end (necesita la API y la base de datos levantadas)
cd tests/e2e
npm ci; npx playwright install chromium
npx playwright test                           # levanta ng serve; con E2E_BASE_URL usa un entorno ya desplegado
```

Con el entorno levantado:
- Web: `http://localhost:8081` (nginx; reenvía `/api` a la API, mismo origen)
- API: `http://localhost:8080` — `/health/live`, `/health/ready`, `/openapi/v1.json` (solo Development)
- Administrador de plataforma: el correo y la clave de `PLATFORM_ADMIN_*` en `infra/compose/.env`, con el gimnasio `platform`. Desde ahí se crean los gimnasios.

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
- `FitCore.Architecture.Tests` falla si se rompen los límites entre capas o se usan APIs que saltan el filtro de tenant.
- Un test falla si un endpoint no declara su autorización.
- Los secretos nunca van en el repositorio (`.env` está ignorado). Modelo de seguridad: ADR 0006; frontend: ADR 0007.
