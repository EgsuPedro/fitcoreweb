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
- API: `http://localhost:8080` — `/health/live`, `/health/ready`
- Solo en Development: Swagger UI en `http://localhost:8080/swagger` (botón *Authorize* con el access token) y OpenAPI en `/openapi/v1.json`
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
| Alumnos (Fase 3, en curso) | `GET/POST /members`, `GET/PUT /members/{id}`, `PATCH /members/{id}/status` |
| Planes y promociones | `GET/POST /membership-plans`, `GET/PUT /membership-plans/{id}`, `GET/POST /promotions`, `GET/PUT /promotions/{id}` |
| Membresías y obligaciones | `GET/POST /memberships`, `GET /memberships/expiring`, `GET /memberships/{id}[/events]`, `POST /memberships/{id}/renew\|freeze\|reactivate\|cancel`, `GET/POST /payment-obligations`, `GET /payment-obligations/{id}`, `POST /payment-obligations/{id}/void` |
| Caja y cartera | `GET/POST /cash-registers`, `PUT /cash-registers/{id}`, `POST /cash-registers/{id}/open\|close\|movements` (exigen `Idempotency-Key`), `GET /cash-registers/{id}/movements\|sessions[/{id}]`, `GET /receivables/summary` |
| Asistencia | `GET /attendance[/{id}]`, `POST /attendance/validate-access\|check-in\|check-out`, `POST /members/{id}/qr-token` |
| Pagos | `GET/POST /payments` (exige `Idempotency-Key`), `GET /payments/{id}[/receipt]`, `POST /payments/{id}/confirm\|reverse` |
| Entrenadores (Fase 4) | `GET/POST /trainers`, `GET /trainers/me`, `GET/PUT /trainers/{id}`, `PATCH /trainers/{id}/status`, `GET/PUT /trainers/{id}/availability`, `GET/POST /trainers/{id}/members`, `POST /trainers/{id}/members/{memberId}/unassign`, `GET /members/{id}/trainer-assignments`, `GET/POST /members/{id}/trainer-notes` |
| Ejercicios (Fase 4) | `GET/POST /exercises`, `GET/PUT /exercises/{id}`, `PATCH /exercises/{id}/status`, `POST /exercises/{id}/duplicate`; catálogo base en `/platform/exercises` |
| Portal del alumno | `POST /members/{id}/access`, `POST /members/{id}/access/reset`, `PATCH /members/{id}/access`, `GET /me/member|memberships|payments|attendance` |
| Avisos de WhatsApp | `GET /notifications`, `GET/PUT /notification-settings`, `PUT /members/{id}/whatsapp-consent` |
| Políticas e IVA | `GET/PUT /commercial-settings`, `GET /tax-rates/current`, `GET/POST /platform/tax-rates` (solo plataforma) |

Errores en `application/problem+json` con un `code` estable; validación con 422 y detalle por campo.

**Fase 3** (casi cerrada) (módulo `FitCore.Commercial`, [ADR 0008](docs/adr/0008-modulo-commercial.md)): hecho el módulo base, los alumnos, los planes y promociones (con tope de descuento), las políticas del gimnasio y las tarifas de IVA editables por decreto (15 % por defecto). Hechas también las membresías (inscribir, renovar, congelar, cancelar) con su obligación de pago. Hechos también los cobros (pagos completos y abonos, transferencias con verificación, reversos, idempotencia, recibo interno). Hecha también la caja (apertura, movimientos, cierre con arqueo, efectivo) y la cartera. Hecha también la asistencia (entrada/salida manual o por QR, política de acceso con deuda, excepciones autorizadas). Hechas las pantallas de Angular de todo lo anterior y sus pruebas end-to-end. El bloque 4b (avisos de WhatsApp por vencimiento) está hecho con un **emisor simulado**: genera y registra los mensajes, pero no envía nada real hasta conectar la cuenta de Meta. Resumen completo en [docs/informe-fase-3.md](docs/informe-fase-3.md).

## Documentación
- [Guía de ejecución y validación manual](docs/execution-guide.md): levantar, datos y usuarios de demostración (`scripts/demo-seed.ps1`), qué comprobar
- [Arquitectura](docs/architecture.md): estructura, auditoría, primer administrador, configuración y ambientes
- [Modelo de datos](docs/database-model.md): esquemas, aislamiento, migraciones (aplicar y comprobar)
- [Matriz de permisos](docs/permissions-matrix.md): catálogo, roles y permiso de cada endpoint
- [Informe de la Fase 3](docs/informe-fase-3.md): archivos, migraciones, endpoints, pantallas, pruebas y pendientes
- [Hoja de ruta](docs/implementation-roadmap.md): estado, pendientes y fases siguientes
- [ADR](docs/adr/): decisiones y su justificación

## Reglas
- El build trata las advertencias como errores.
- `FitCore.Architecture.Tests` falla si se rompen los límites entre capas o se usan APIs que saltan el filtro de tenant.
- Un test falla si un endpoint no declara su autorización.
- Los secretos nunca van en el repositorio (`.env` está ignorado). Modelo de seguridad: ADR 0006; frontend: ADR 0007.
