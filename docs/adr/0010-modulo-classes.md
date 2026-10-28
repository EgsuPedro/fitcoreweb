# ADR 0010 — Módulo Classes (clases grupales, horarios, reservas, lista de espera y notificaciones internas)

**Estado:** Aprobado el 2026-10-28 (Fase 5 en nuestra numeración; «FASE 4» del prompt del usuario). Se implementa por bloques; cada bloque marca aquí lo realmente hecho.

## Diagnóstico del repositorio (bloque 1)
| Necesidad de la fase | Situación | Acción |
|---|---|---|
| Alumnos, sucursales, permisos, auditoría, RLS, filtro de tenant, `MemberScope`, Result/Dispatcher | Existen | Se reutilizan; misma API y misma base |
| Instructor | Existe `TrainerProfile` (módulo Wellness) con disponibilidad semanal por franja y sucursal opcional | El instructor es un entrenador. Se lee su disponibilidad por una vista de solo lectura (los módulos no se referencian). Si no tiene franjas, no se restringe; si las tiene, la sesión debe caber en una |
| Membresía activa y política de acceso con deuda | Existen en Commercial (`AccessPolicy`, `AccessRules`, membresías con estado y fechas) | La elegibilidad de una clase reutiliza **la misma política** que la asistencia, leyendo membresías y obligaciones por vistas de solo lectura; nunca se duplica la regla en Angular |
| Asistencia | `commercial.attendance` registra entradas **al gimnasio** (check-in/out, QR, excepciones) | **No se modifica.** La asistencia a una clase es un registro propio (`class_attendance`) ligado a la reserva; una reserva y una asistencia son conceptos distintos. Opcionalmente referencia la entrada al gimnasio del mismo día, sin escribirla |
| Notificaciones | Existe `commercial.notifications` (WhatsApp por **vencimiento de membresía**, emisor simulado, reintentos, reclamo por el Worker) | Es específico de membresías. Se crea una **bandeja interna** nueva (`classes.inbox_notifications`) con preferencias por usuario; el canal WhatsApp no se amplía (no hay credenciales de Meta) |
| Tareas en segundo plano | Existen: `OutboxProcessor` (eventos de dominio con `IOutboxEventHandler`) y `ExpiryNotificationWorker` en `FitCore.Worker` | Se reutiliza el Worker para vencer invitaciones de lista de espera, generar recordatorios y entregar notificaciones. No se agrega infraestructura nueva |
| Idempotencia | Existe el patrón `Idempotency-Key` en pagos y caja | Se reutiliza para reservar y cancelar |
| Calendario Angular | No hay librería de calendario | Calendario propio (vistas día/semana/mes) sin dependencias nuevas, como los gráficos SVG de la fase anterior |
| Portal del alumno | Existe `my-portal` con `fc-member-wellness` | Se amplía con una sección de clases; no se crea otro portal |

## Decisiones (confirmadas por el usuario el 2026-10-28)
1. **Módulo nuevo `FitCore.Classes`**, con su `DbContext`, esquema `classes`, RLS forzado, claves compuestas con `tenant_id` y migraciones propias. Lee alumnos, membresías, obligaciones y entrenadores por vistas de solo lectura (`*Ref`); lo vigila la prueba de arquitectura.
2. **Lista de espera con invitación con plazo.** Al liberarse un cupo se **reserva** para el primero en espera, que recibe una notificación y tiene un plazo (por clase, p. ej. 30 min) para aceptar; si vence o rechaza, el Worker pasa el cupo al siguiente elegible. El cupo reservado cuenta como ocupado, así nadie más puede tomarlo por la puerta de atrás.
3. **Instructor = entrenador** existente (`TrainerProfile`).
4. **Notificaciones:** bandeja interna con preferencias; el registro (evento) se separa de la entrega (Worker). Correo, SMS, WhatsApp y push **no** se implementan: sin proveedor ni credenciales no se simula ningún envío exitoso. El diseño deja un puerto `INotificationChannel` por canal.
5. **Fuera de la fase:** Flutter, facturación SRI, nómina y contabilidad.

## Diseño previsto (resumen)
- **Dominio:** `ActivityType` (catálogo: yoga, spinning, funcional, zumba, pilates u otros del gimnasio), `GroupClass` (definición: instructor, sucursal, duración, capacidad, nivel, requisitos, anticipación mínima/máxima de reserva, plazo de cancelación, política de espera, elegibilidad), `ClassSession` (instancia concreta con fecha/hora, capacidad efectiva, estado `Scheduled|Cancelled|Completed`, `series_id` opcional), `ClassSeries` (regla de recurrencia que **genera sesiones concretas**), `Reservation` (estado `Confirmed|Waitlisted|Offered|Cancelled|Expired`), `ClassAttendance` (`Attended|NoShow`, uno por reserva), `InboxNotification`, `NotificationPreference`.
- **Concurrencia:** la capacidad se protege con un candado consultivo transaccional por sesión (`pg_advisory_xact_lock`) más una restricción única parcial `(session_id, member_id)` para reservas vivas; el recuento y la inserción ocurren en la misma transacción. Prueba de concurrencia real contra PostgreSQL.
- **Superposición del instructor:** restricción `EXCLUDE USING gist` sobre `(tenant, instructor, rango de tiempo)` para sesiones no canceladas, además de la validación en el dominio.
- **Recurrencias idempotentes:** clave única `(series_id, starts_at)`; regenerar no duplica.
- **Auditoría:** reservas, cancelaciones, promociones, excepciones aprobadas y cambios de sesión, sin contenido sensible.

## Bloques
| Bloque | Contenido | Estado |
|---|---|---|
| 1 | Diagnóstico y decisiones (este documento) | Hecho |
| 2 | Módulo `Classes`: actividades y clases (CRUD, estado, validaciones, permisos, migraciones, RLS) | Pendiente |
| 3 | Sesiones: individuales, recurrencias idempotentes, superposición y disponibilidad del instructor, cancelación | Pendiente |
| 4 | Reservas: elegibilidad, capacidad, plazos, cancelación, idempotencia, concurrencia | Pendiente |
| 5 | Lista de espera: invitaciones con plazo, vencimiento y promoción (Worker) | Pendiente |
| 6 | Asistencia a clase (participantes, asistió/no asistió, historial) | Pendiente |
| 7 | Notificaciones internas: eventos, entrega, preferencias, reintentos, recordatorios | Pendiente |
| 8 | Angular del personal: clases, calendario, series, detalle de sesión, reservas, espera, asistencia | Pendiente |
| 9 | Portal del alumno: agenda, reservar, cancelar, espera, historial, notificaciones | Pendiente |
| 10 | Pruebas, regresión y documentación | Pendiente |
