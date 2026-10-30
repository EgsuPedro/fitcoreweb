# FIT360

Plataforma SaaS para la administración integral de gimnasios (Ecuador, USD).

**Estado:** Fases 1 a 4 completas. Identidad y multiempresa; comercial (alumnos, planes, membresías, cobros, caja, asistencia); y bienestar (entrenadores, ejercicios, rutinas, nutrición, evaluaciones, objetivos, fotos de progreso privadas), con su interfaz Angular. Pendientes: Flutter, facturación electrónica SRI, inventario/punto de venta, reportes, WhatsApp real y recuperación de contraseña.

## Estructura

```
src/backend/
  FitCore.Api/                   Host HTTP: endpoints, JWT, autorización, ProblemDetails, OpenAPI
  FitCore.Worker/                Procesos en segundo plano (outbox)
  BuildingBlocks/                SharedKernel, Application.Core (despachador, contratos), Infrastructure.Core
  Modules/Commercial/FitCore.Commercial/   Alumnos, planes, membresías, cobros, caja, asistencia, avisos
  Modules/Wellness/FitCore.Wellness/       Entrenadores, ejercicios, rutinas, nutrición, evaluaciones, objetivos, fotos
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
| Rutinas (Fase 4) | `GET/POST /training-plans`, `GET/PUT /training-plans/{id}[/versions]`, `POST …/duplicate|assignments`, `PATCH …/status`, `POST /training-assignments/{id}/end`, `GET /members/{id}/training-plan|workout-sessions|exercise-progress|training-adherence`, `POST /workout-sessions` |
| Nutrición (Fase 4) | `GET/POST /nutrition-plans`, `GET/PUT /nutrition-plans/{id}[/versions]`, `POST …/duplicate|assignments`, `GET /members/{id}/nutrition-plan|meal-logs|nutrition-adherence`, `GET/POST /members/{id}/dietary-profile`, `POST /meal-logs` |
| Evaluaciones y objetivos (Fase 4) | `GET/POST /members/{id}/assessments` (`/compare`, `/progress`), `GET/POST /members/{id}/goals`, `POST /goals/{id}/close` |
| Fotos de progreso (Fase 4) | `GET /members/{id}/photo-consent`, `POST …/photo-consent/grant|revoke`, `GET/POST /members/{id}/progress-photos`, `GET /progress-photos/{id}/content`, `DELETE /progress-photos/{id}` |
| Clases grupales (Fase 5) | `GET/POST /group-classes`, `GET/PUT /group-classes/{id}`, `PATCH …/status`; `GET/POST /class-sessions`, `GET/PATCH /class-sessions/{id}`, `POST …/cancel|complete`, `GET …/roster`, `PUT …/attendance`; series en `/class-sessions/recurrences` (`POST …/{id}/generate|cancel`) |
| Reservas y asistencia a clase (Fase 5) | `GET/POST /class-reservations`, `POST …/{id}/cancel|accept|decline`, `GET /class-attendance` |
| Portal del alumno: clases (Fase 5) | `GET /me/class-sessions`, `GET/POST /me/class-reservations`, `POST …/{id}/cancel|accept|decline`, `GET /me/class-attendance` |
| Avisos internos (Fase 5) | `GET /me/notifications` (`/summary`, `PATCH …/{id}/read`, `POST …/read-all`), `GET/PUT /me/notification-preferences` |
| Dashboard y reportes (Fase 6) | `GET /analytics/context`, `/analytics/dashboard` (+`/comparison`); financieros `GET /reports/financial/collections|payment-methods|outstanding-balances|cash-registers|reversals`; alumnos `GET /reports/members/summary|acquisitions|retention|renewals|expirations`; operación `GET /reports/attendance`, `/reports/classes/occupancy|cancellations`; `GET /reports/branches/comparison`. Filtros comunes: `preset` (Today, Last7Days, Last30Days, CurrentMonth, PreviousMonth, CurrentYear, Custom), `from`/`to`, `branchIds` |
| Planes y suscripciones SaaS (Fase 7) | `GET/POST /platform/plans`, `GET/PUT /platform/plans/{id}`, `PATCH …/status`, `GET /platform/plans/catalog`; `GET/POST /platform/tenants/{id}/subscription`, `GET …/subscription/history`, `POST …/subscription/activate|suspend|reactivate`, `GET /platform/subscriptions`; `POST /platform/tenants` acepta `planCode` y `startTrial`; `POST …/subscription/renew|change-plan|cancel|resume-cancellation|extend`, `POST /platform/subscriptions/run-lifecycle`; cobros comerciales `POST …/subscription/invoices`, `GET /platform/billing/invoices[/{id}]`, `POST …/invoices/{id}/pay|attempts|void`, `GET /platform/billing/payments`, `POST …/payments/{id}/refund`, `GET /platform/billing/summary`; portal del gimnasio `GET /my/subscription`, `/usage`, `/plans`, `/invoices`, `/notices`, `POST /notices[/{id}]/read`, `GET|POST /requests`, `POST /requests/{id}/withdraw`; solicitudes para la plataforma `GET /platform/subscription-requests`, `POST …/{id}/approve|reject`; proveedor de pagos `POST /webhooks/payments` (anónimo, firmado con HMAC; 503 si no hay proveedor), `GET /platform/billing/provider`, `GET /platform/billing/provider-events` |
| Exportaciones CSV (Fase 6) | `POST /reports/exports`, `GET /reports/exports`, `GET /reports/exports/{id}`, `GET …/{id}/download` |
| Políticas e IVA | `GET/PUT /commercial-settings`, `GET /tax-rates/current`, `GET/POST /platform/tax-rates` (solo plataforma) |

Errores en `application/problem+json` con un `code` estable; validación con 422 y detalle por campo.

**Fase 3** (casi cerrada) (módulo `FitCore.Commercial`, [ADR 0008](docs/adr/0008-modulo-commercial.md)): hecho el módulo base, los alumnos, los planes y promociones (con tope de descuento), las políticas del gimnasio y las tarifas de IVA editables por decreto (15 % por defecto). Hechas también las membresías (inscribir, renovar, congelar, cancelar) con su obligación de pago. Hechos también los cobros (pagos completos y abonos, transferencias con verificación, reversos, idempotencia, recibo interno). Hecha también la caja (apertura, movimientos, cierre con arqueo, efectivo) y la cartera. Hecha también la asistencia (entrada/salida manual o por QR, política de acceso con deuda, excepciones autorizadas). Hechas las pantallas de Angular de todo lo anterior y sus pruebas end-to-end. El bloque 4b (avisos de WhatsApp por vencimiento) está hecho con un **emisor simulado**: genera y registra los mensajes, pero no envía nada real hasta conectar la cuenta de Meta. Resumen completo en [docs/informe-fase-3.md](docs/informe-fase-3.md).

**Fase 4** (completa) (módulo `FitCore.Wellness`, [ADR 0009](docs/adr/0009-modulo-wellness.md)): entrenadores con alumnos asignados y observaciones profesionales, biblioteca de 42 ejercicios base más los propios, rutinas y planes nutricionales versionados, registro de sesiones y comidas por el propio alumno, evaluaciones con IMC y comparación, objetivos, adherencia y fotos de progreso privadas con consentimiento. Los datos sensibles (nutrición, alergias, evaluaciones, objetivos, fotos, observaciones) **no** se conceden por defecto al Propietario ni al AdministradorSucursal, y toda lectura de personal queda auditada. Las fotos se guardan en un volumen privado (`FileStorage:RootPath`, volumen `fitcore_files` en Docker). Resumen en [docs/informe-fase-4.md](docs/informe-fase-4.md).

**Fase 5** (completa) (módulo `FitCore.Classes`, [ADR 0010](docs/adr/0010-modulo-classes.md)): clases grupales con reglas configurables, sesiones sueltas y series recurrentes idempotentes, control de superposición y disponibilidad del instructor, reservas con cupo protegido bajo concurrencia, lista de espera con invitación con plazo (la vence y promueve el Worker), asistencia a clase, bandeja de avisos internos con preferencias, calendario de Angular para el personal y sección «Mis clases» en el portal del alumno. **Solo existe el canal interno**: no se envían correos, SMS, WhatsApp ni push (no hay proveedor). Requiere el servicio `worker`. Resumen en [docs/informe-fase-5.md](docs/informe-fase-5.md); validación manual en [docs/guia-pruebas-fase-5.md](docs/guia-pruebas-fase-5.md).

**Fase 6** (completa) (módulo `FitCore.Analytics`, solo lectura, [ADR 0011](docs/adr/0011-modulo-analytics.md)): panel por rol, reportes financieros, de alumnos y membresías, de asistencia y clases, comparación de sucursales y exportación a CSV (24 h de vigencia, tope de 20.000 filas), con 46 indicadores definidos ([catálogo](docs/analytics-indicadores.md)). Todo se calcula en PostgreSQL con los datos reales, filtrado por el alcance del usuario en el servidor; no hay cachés ni tablas de resumen. Lo cobrado **no** es utilidad. Exportación a PDF diferida. Resumen en [docs/informe-fase-6.md](docs/informe-fase-6.md); validación manual en [docs/guia-pruebas-fase-6.md](docs/guia-pruebas-fase-6.md).

**Fase 7** (completa) (módulo `FitCore.Subscriptions`, [ADR 0012](docs/adr/0012-modulo-subscriptions.md)): planes comerciales con versiones, funciones y límites; suscripciones con prueba, gracia, renovación, cambio de plan, cancelación y prórroga; límites de sucursales, usuarios y alumnos hechos cumplir en PostgreSQL; modo restringido que conserva el cobro a los alumnos; facturas y pagos comerciales internos; panel global de plataforma con métricas (MRR contratado, no cobrado); portal «Mi suscripción» con solicitudes y avisos internos; y la abstracción del proveedor de pagos con webhooks firmados e idempotentes. **No hay cobro automático, ni proveedor de pagos real, ni facturación tributaria, ni avisos por correo/SMS/WhatsApp.** El dominio comercial SaaS está separado de los pagos y membresías de los alumnos. Resumen en [docs/informe-fase-7.md](docs/informe-fase-7.md); validación manual en [docs/guia-pruebas-fase-7.md](docs/guia-pruebas-fase-7.md).

**Marca del gimnasio** ([identidad visual](docs/identidad-visual.md)): paleta FIT360 (teal y naranja suave), tipografía Inter, mascota Fiti; cada gimnasio carga su **logo** (PNG o JPG ≤ 1 MB, sin metadatos) que aparece en el menú y en los **libros de Excel** de los reportes (formato por defecto de las exportaciones; CSV sigue disponible). El mismo contrato servirá para los comprobantes.

**Fase 8** (en curso) (sitio comercial, registro de gimnasios y onboarding, [ADR 0013](docs/adr/0013-sitio-comercial-registro-y-onboarding.md)): sitio público prerenderizado, registro del propietario con verificación de correo, recuperación de clave, asistente de configuración e invitaciones de empleados. Correo por SMTP real (sin simulaciones). Bloque 1 (diagnóstico y diseño) hecho.

## Documentación
- [Guía de ejecución y validación manual](docs/execution-guide.md): levantar, datos y usuarios de demostración (`scripts/demo-seed.ps1`), qué comprobar
- [Arquitectura](docs/architecture.md): estructura, auditoría, primer administrador, configuración y ambientes
- [Modelo de datos](docs/database-model.md): esquemas, aislamiento, migraciones (aplicar y comprobar)
- [Matriz de permisos](docs/permissions-matrix.md): catálogo, roles y permiso de cada endpoint
- [Informe de la Fase 3](docs/informe-fase-3.md): archivos, migraciones, endpoints, pantallas, pruebas y pendientes
- [Informe de la Fase 4](docs/informe-fase-4.md): bienestar, privacidad, migraciones, endpoints, pruebas y pendientes
- [Informe de la Fase 5](docs/informe-fase-5.md): clases, reservas, espera, asistencia, avisos, pruebas y pendientes
- [Guía de pruebas de la Fase 5](docs/guia-pruebas-fase-5.md): validación manual pantalla por pantalla
- [Informe de la Fase 6](docs/informe-fase-6.md): analítica, reglas, rendimiento medido, pruebas y pendientes
- [Indicadores de analítica](docs/analytics-indicadores.md): definición de cada indicador
- [Guía de pruebas de la Fase 6](docs/guia-pruebas-fase-6.md): validación manual con datos de práctica
- [Informe de la Fase 7](docs/informe-fase-7.md): suscripciones SaaS, límites, cobros comerciales, panel, portal, proveedor de pagos, pruebas, seguridad y pendientes
- [Guía de pruebas de la Fase 7](docs/guia-pruebas-fase-7.md): validación manual con datos
- [Despliegue](docs/despliegue.md): subir una demo pública (máquina gratuita, HTTPS, actualización desde git, copias de seguridad)
- [Hoja de ruta](docs/implementation-roadmap.md): estado, pendientes y fases siguientes
- [ADR](docs/adr/): decisiones y su justificación

## Reglas
- El build trata las advertencias como errores.
- `FitCore.Architecture.Tests` falla si se rompen los límites entre capas o se usan APIs que saltan el filtro de tenant.
- Un test falla si un endpoint no declara su autorización.
- Los secretos nunca van en el repositorio (`.env` está ignorado). Modelo de seguridad: ADR 0006; frontend: ADR 0007.
