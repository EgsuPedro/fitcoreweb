# Informe de la Fase 5 — Clases grupales, horarios, reservas y notificaciones

Corresponde a la «FASE 4» del prompt del usuario (nuestra numeración: 5). Módulo `FitCore.Classes`. Decisiones y reglas: [ADR 0010](adr/0010-modulo-classes.md). Guía de validación manual pantalla por pantalla: [guia-pruebas-fase-5.md](guia-pruebas-fase-5.md). Fuera de la fase: Flutter, facturación electrónica SRI, nómina y contabilidad.

## Qué se entrega
| Bloque | Contenido | Estado |
|---|---|---|
| 1 | Diagnóstico del repositorio y ADR 0010 | Hecho |
| 2 | Módulo `Classes`: actividades y clases con reglas configurables (cupo, plazos, espera, elegibilidad) | Hecho |
| 3 | Sesiones sueltas y series recurrentes idempotentes; superposición y disponibilidad del instructor; cancelación | Hecho |
| 4 | Reservas: elegibilidad, capacidad, plazos, conflicto de horario, cancelación, idempotencia, concurrencia | Hecho |
| 5 | Lista de espera con invitación con plazo, vencimiento y promoción (Worker) | Hecho |
| 6 | Asistencia a clase (asistió / no asistió), correcciones, completar sesión, historial | Hecho |
| 7 | Notificaciones internas: eventos por outbox, bandeja, preferencias, recordatorios, reintentos | Hecho |
| 8 | Angular del personal: clases, calendario día/semana/mes, series, panel de sesión, reservas, espera, asistencia, campana y bandeja | Hecho |
| 9 | Portal del alumno: agenda, reservar, cancelar, espera, cupo ofrecido, historial | Hecho |
| 10 | Regresión, documentación y guía de pruebas | Hecho |

## Decisiones principales (confirmadas el 2026-10-28)
- Módulo propio `FitCore.Classes` (esquema `classes`); lee alumnos, membresías, obligaciones y entrenadores por vistas de solo lectura, vigilado por pruebas de arquitectura.
- Instructor = `TrainerProfile` existente. Elegibilidad = **la misma política de acceso** que la asistencia al gimnasio (nunca duplicada en Angular).
- Lista de espera **con invitación con plazo**: al liberarse un cupo se reserva para el primero elegible; ese cupo cuenta como ocupado hasta que acepte, rechace o venza.
- Asistencia a clase = registro propio ligado a la reserva; `commercial.attendance` (entradas al gimnasio) **no se modifica**.
- Notificaciones: bandeja interna + preferencias; el registro (evento) se separa de la entrega. Correo, SMS, WhatsApp y push **no existen**: sin proveedor ni credenciales no se simula ningún envío.

## Cómo se garantiza la corrección
- **Capacidad y concurrencia:** se bloquea la fila de la sesión (`SELECT … FOR UPDATE`) y un disparador en la base impide ocupar más cupos que la capacidad (`FC001`), reservar en una sesión no programada (`FC002`), bajar la capacidad por debajo de lo ocupado (`FC003`), esperar sin lista (`FC004`) o con la fila llena (`FC005`). Único parcial `(sesión, alumno)` para reservas vivas.
- **Instructor:** `EXCLUDE USING gist` sobre `(gimnasio, instructor, rango de tiempo)` para sesiones no canceladas: no puede dar dos sesiones a la vez, ni con solicitudes simultáneas.
- **Series:** generan sesiones concretas; único `(serie, hueco original)`: regenerar o extender no duplica ni resucita sesiones canceladas o movidas.
- **Idempotencia:** reservar acepta `Idempotency-Key` (tabla `classes.idempotency_keys`); el doble clic no reserva dos veces.
- **Avisos sin duplicar:** único `(gimnasio, destinatario, clave anti-duplicado)` con `INSERT … ON CONFLICT DO NOTHING`; los eventos viajan por la outbox en la misma transacción del cambio.
- **Aislamiento:** RLS forzada por gimnasio, claves compuestas `(tenant_id, id)`, triggers de inmutabilidad y sin `DELETE` para el rol de la aplicación. Una sesión de otra sucursal responde 404.
- **Alumno:** todos los endpoints `/me/…` resuelven al alumno desde su sesión; un `memberId` en el cuerpo se ignora.
- **Entrenador:** registra asistencia solo de las sesiones donde es instructor.

## Migraciones (esquema `classes`)
`InitialClasses` → `AddGroupClassesSecurity` → `AddSessions` → `AddSessionsSecurity` → `AddReservations` → `AddReservationsSecurity` → `AddWaitlist` → `AddWaitlistSecurity` → `AddClassAttendance` → `AddClassAttendanceSecurity` → `AddNotifications` → `AddNotificationsSecurity`.

## Endpoints (bajo `/api/v1`)
- **Clases:** `GET/POST /group-classes`, `GET/PUT /group-classes/{id}`, `PATCH …/status`.
- **Sesiones y series:** `GET/POST /class-sessions`, `GET/PATCH /class-sessions/{id}`, `POST …/cancel|complete`, `GET …/roster`, `PUT …/attendance`; `GET/POST /class-sessions/recurrences`, `GET …/{id}`, `POST …/{id}/generate|cancel`.
- **Reservas (personal):** `GET/POST /class-reservations`, `GET …/{id}`, `POST …/{id}/cancel|accept|decline`.
- **Asistencia:** `GET /class-attendance`.
- **Portal del alumno:** `GET /me/class-sessions` (agenda de su sucursal con su propia reserva), `GET/POST /me/class-reservations`, `POST …/{id}/cancel|accept|decline`, `GET /me/class-attendance`; `GET /me/member` ahora incluye `branchTimeZone`.
- **Avisos del propio usuario:** `GET /me/notifications` (+`/summary`, `PATCH …/{id}/read`, `POST …/read-all`), `GET/PUT /me/notification-preferences`.

## Procesos en segundo plano (`FitCore.Worker`)
- `OutboxProcessor` (cada 5 s): convierte los eventos de reservas, espera y sesiones en avisos de la bandeja.
- `WaitlistWorker` (`Classes:WaitlistIntervalSeconds`, 30 s): vence invitaciones, ofrece el cupo al siguiente, completa cupos libres y cierra la fila de sesiones empezadas.
- `NotificationWorker` (`Classes:NotificationIntervalSeconds`, 60 s): recordatorios (`Classes:ReminderLeadMinutes`, 180) y entrega con reintentos 1 min / 5 min / 30 min / 2 h (máx. 5).
- **Sin el Worker no se generan avisos ni vencen las invitaciones.**

## Pantallas (Angular, español por defecto, inglés disponible)
- **Personal:** *Clases* (alta, edición y estado), *Horario* (calendario día/semana/mes en la hora de la sucursal, programar sesión, serie recurrente, panel de sesión con Participantes / Reservar / Lista de espera, asistencia, finalizar o cancelar), *Historial de clases*, campana de avisos y bandeja *Mis avisos* con preferencias.
- **Alumno:** sección *Mis clases* en su portal (Agenda, Mis reservas, Historial) y la misma campana.

## Permisos nuevos
`group-classes.read|manage`, `class-sessions.read|manage`, `class-reservations.read|manage`, `class-attendance.read|manage`, `inbox.use` (todos los usuarios; el alumno incluido). Ver la [matriz de permisos](permissions-matrix.md). Los tenants creados antes de la fase necesitan volver a ejecutar `scripts/demo-seed.ps1` para recibirlos.

## Pruebas (resultados de la regresión final)
Regresión del 2026-10-28, en local, contra PostgreSQL real:

| Suite | Resultado |
|---|---|
| Unitarias SharedKernel / Classes / Commercial / Wellness | 13 / 57 / 144 / 131 correctas |
| Arquitectura (límites entre módulos, filtros de tenant, endpoints con autorización, paridad de políticas) | 22 correctas |
| Integración (API + PostgreSQL con Testcontainers) | 483 correctas, 0 fallos en la segunda corrida completa (4 min 22 s); la primera tuvo 482 + 1 intermitente (ver pendientes) |
| Angular: Vitest | 72 correctas |
| Angular: claves de traducción es/en | 929 claves, todas presentes |
| Angular: compilación de producción (ng build) | correcta |
| e2e Playwright (navegador real, API, base y Worker reales) | 36 correctas (30 previas + 3 de clases del personal + 3 del portal del alumno) |

Cubre, entre otras cosas: aislamiento entre gimnasios y sucursales, cupo bajo concurrencia, reservas idénticas simultáneas, series idénticas simultáneas, superposición del instructor, promoción de la lista de espera al cancelar, vencimiento de ofertas, asistencia solo de sesiones empezadas, avisos sin duplicar y alcance del alumno y del entrenador.

## Pendiente y límites conocidos
- **Canales externos** (correo, SMS, WhatsApp, push): sin proveedor. El puerto `INotificationChannel` existe; un canal sin configurar deja el aviso en «Fallido: canal no disponible», nunca se simula un éxito. Credenciales irán por configuración, nunca en el repositorio.
- **Sesiones pasadas** no se completan solas: hoy se usa «Finalizar sesión».
- **Retención de avisos:** no hay tarea que limpie la bandeja antigua.
- **Agenda del alumno:** muestra las próximas 50 sesiones; el endpoint acepta `from`, `to` y `classId` pero la pantalla aún no ofrece esos filtros.
- Las reglas de elegibilidad se aplican siempre en el servidor; la pantalla del alumno no las anticipa, muestra el motivo que devuelve la API.
- **Prueba intermitente observada:** en una corrida completa, `Regenerar_o_crear_la_misma_serie_en_paralelo_no_duplica_sesiones` respondió una vez 500 en la parte de cuatro series idénticas simultáneas (no hubo duplicados). No se reprodujo en 9 repeticiones posteriores, ni bajo carga, ni en la segunda corrida completa (483 de 483); el servidor reintenta ya choques de unicidad, exclusión, versión e interbloqueo. Si reaparece, activar el registro detallado del servidor de pruebas para ver la causa.
- Heredado de fases anteriores: envío real de WhatsApp (cuenta de Meta), recuperación de contraseña (proveedor de correo), facturación SRI, inventario y app móvil.
