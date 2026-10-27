# Informe de la Fase 3 — Comercial (alumnos, planes, membresías, cobros, caja, asistencia)

Corresponde a la «FASE 2» del prompt del usuario (nuestra numeración: 3). Decisiones y reglas: [ADR 0008](adr/0008-modulo-commercial.md).

## Qué se entrega
| Bloque | Contenido | Estado |
|---|---|---|
| 1 | Módulo `FitCore.Commercial`, `DbContext`, RLS, permisos | Hecho |
| 2 | Alumnos (cédula/RUC/pasaporte validados, código por gimnasio, QR) | Hecho |
| 3 | Planes, promociones con tope, políticas del gimnasio, tarifas de IVA con vigencia | Hecho |
| 4 | Membresías (inscribir, renovar, congelar, reactivar, cancelar) + obligación de pago | Hecho |
| 5 | Obligaciones, pagos, abonos, transferencias por verificar, reversos, idempotencia, recibo | Hecho |
| 6 | Caja (apertura, movimientos, cierre con arqueo, efectivo) y cartera | Hecho |
| 7 | Asistencia (entrada/salida, QR, política de acceso con deuda, excepciones auditadas) | Hecho |
| 8 | Angular: todas las pantallas anteriores | Hecho |
| 9 | e2e, regresión, CI más rápido, documentación | Hecho |
| 4b | Avisos de WhatsApp por vencimiento | **Pendiente** (necesita cuenta y credenciales de Meta) |

## Archivos principales
- **Backend:** `src/backend/Modules/Commercial/FitCore.Commercial/` (Domain, Application por función, Persistence con configuraciones y migraciones); `src/backend/FitCore.Api/Endpoints/CommercialEndpoints.cs`; permisos y plantillas de rol en el módulo `Platform`.
- **Frontend:** `src/web/src/app/features/{members,memberships,plans,settings,billing,cash,attendance}`, componentes compartidos en `src/web/src/app/shared`, cliente de la API en `core/api`, traducciones `public/i18n/{es,en}.json`.
- **Pruebas:** `tests/FitCore.Commercial.UnitTests`, `tests/FitCore.Api.IntegrationTests`, `tests/FitCore.Architecture.Tests`, `src/web/**/*.spec.ts`, `tests/e2e/tests/{app,commercial}.spec.ts`.
- **Docs:** ADR 0008, `permissions-matrix.md`, `database-model.md`, `execution-guide.md`, `implementation-roadmap.md`, README.
- **Otros:** `scripts/demo-seed.ps1` (datos demo y sincronización de roles), `src/web/scripts/check-i18n-keys.mjs`.

## Migraciones (esquemas `commercial`, `billing`, `access`)
`InitialCommercial` → `AddMembersSecurity` → `AddPlansPromotionsTaxes` → `AddPlansSecurity` → `AddMembershipsAndObligations` → `AddMembershipsSecurity` → `AddPayments` → `AddPaymentsSecurity` → `AddCash` → `AddCashSecurity` → `AddAttendance` → `AddAttendanceSecurity`.
Todas con RLS forzada por gimnasio, claves compuestas (tenant, id), tablas de dinero y trazas solo-anexado (sin UPDATE/DELETE para el rol de la aplicación) y triggers de respaldo (inmutabilidad, sobre-asignación de pagos).

## Endpoints (todos bajo `/api/v1`, con permiso y filtro de gimnasio/sucursal)
- **Alumnos:** `GET/POST /members`, `GET/PUT /members/{id}`, `PATCH /members/{id}/status`, `POST /members/{id}/qr-token`.
- **Planes y promociones:** `GET/POST /membership-plans`, `GET/PUT /membership-plans/{id}`, `GET/POST /promotions`, `GET/PUT /promotions/{id}`.
- **Políticas e IVA:** `GET/PUT /commercial-settings`, `GET /tax-rates/current`, `GET/POST /platform/tax-rates`.
- **Membresías:** `GET/POST /memberships`, `GET /memberships/expiring`, `GET /memberships/{id}[/events]`, `POST /memberships/{id}/renew|freeze|reactivate|cancel`.
- **Obligaciones y cartera:** `GET/POST /payment-obligations`, `GET /payment-obligations/{id}`, `POST …/void`, `GET /receivables/summary`.
- **Pagos:** `GET/POST /payments` (`Idempotency-Key`), `GET /payments/{id}[/receipt]`, `POST /payments/{id}/confirm|reverse` (`Idempotency-Key`).
- **Caja:** `GET/POST /cash-registers`, `PUT /cash-registers/{id}`, `POST …/open|close|movements` (`Idempotency-Key`), `GET …/movements`, `GET …/sessions[/{id}]`.
- **Asistencia:** `GET /attendance[/{id}]`, `POST /attendance/validate-access|check-in|check-out`.

## Pantallas (Angular, español por defecto, inglés disponible)
Alumnos (lista y ficha con QR) · Membresías (lista, «Por vencer», detalle con renovar/congelar/reactivar/cancelar) · Cobros (registro, confirmar transferencia, reverso, recibo imprimible) · Cartera · Caja (cajas, apertura, cierre con arqueo y confirmación en modal, movimientos, historial) · Asistencia (validar, entrada, salida, excepción, «dentro ahora», historial) · Planes · Promociones · Políticas comerciales · Tarifas de IVA (plataforma).
Todas usan la API real. El aviso de éxito aparece solo tras la respuesta del servidor; los permisos ocultan menú, botones y rutas, y el backend sigue siendo la autoridad.

## Pruebas
| Capa | Cantidad | Qué cubre |
|---|---|---|
| Backend unitarias + arquitectura | 149 | Reglas de dominio, validación de identificación, límites entre capas |
| Backend integración (PostgreSQL real) | 222 | Aislamiento por gimnasio/sucursal, RLS, permisos, concurrencia de pagos, idempotencia, caja, asistencia |
| Frontend unitarias (Vitest) | 51 | Validación de cédula/RUC, detección de PAN, idempotencia, listados, formato, traducciones |
| Claves de traducción | `npm run check:i18n` | Toda clave usada existe en es y en (en CI) |
| End-to-end (Playwright, API + BD reales) | 19 | Acceso y plataforma (14) y flujo comercial completo (5): alumno → caja → inscripción → cobro en efectivo → asistencia → cierre de caja, más usuario sin permisos |

**Tiempo de CI:** la suite de integración compartía una sola colección xUnit (todo en serie, ~10 min). Ahora son 4 colecciones con su propio PostgreSQL y API, que xUnit ejecuta en paralelo: 222 pruebas en 4 min 22 s en local (antes ~10 min).

## Pendientes
1. **Bloque 4b — WhatsApp** (ver abajo qué se necesita).
2. Cierre automático de entradas olvidadas y registro de intentos de acceso rechazados.
3. Subida de fotos (hoy solo URL).
4. Recuperación de contraseña (requiere proveedor de correo), pantallas de editar gimnasio y cambiar contraseña.
5. Comprobación de que los tipos de la API no se desfasan en CI, pruebas de accesibilidad (axe), CSP con nonces.

## WhatsApp: qué se necesita
Meta WhatsApp Cloud API (negocio verificado, número de teléfono, token permanente, plantillas de mensaje aprobadas) y el texto de consentimiento del alumno (LOPDP). El código (puerto `IWhatsAppSender`, tarea del worker, tabla `notifications`) puede hacerse antes y probarse con un emisor simulado.
