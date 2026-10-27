# ADR 0008: Módulo Commercial (alumnos, membresías, cobros, caja y asistencia)

**Estado:** Aprobado el 2026-10-27 (Fase 3). Se implementa por bloques; cada bloque marca aquí lo realmente hecho.

## Decisiones del usuario
- **Un solo módulo `Commercial` con un `DbContext`.** Inscribir (membresía + obligación) y cobrar (pago + asignación + caja) son transacciones únicas. Refina el ADR 0001 igual que Platform (ADR 0006).
- **El precio de lista incluye IVA** (15 % por defecto, tarifa vigente de la tabla `billing.tax_rates`; ver ADR 0003). El sistema desglosa subtotal e IVA y congela la tarifa en cada obligación.
- **Acceso con deuda:** se permite mientras la obligación no esté vencida. Es un modo de una política configurable por gimnasio (`RequireFullPayment`, `AllowUntilOverdue`, `AllowWithMinimumPayment`); el valor inicial es `AllowUntilOverdue`.
- **Una membresía vigente por alumno.** Renovar crea una membresía nueva que inicia al terminar la actual; la existente no se modifica.

## Supuestos confirmados por el usuario (2026-10-27)
Todos los de la lista siguiente fueron validados. Cambios y añadidos:
- **Notificaciones por WhatsApp (nuevo requisito):** el administrador o la recepcionista deben recibir un aviso por WhatsApp cuando una membresía esté a 3 días de terminar, y después un mensaje cordial. Ver "Notificaciones" abajo.
- **Fotografía:** la carga de archivos se hará en una fase posterior; en esta solo hay URL opcional.

## Supuestos que tomé (confirmados)
- La **fecha de vencimiento de la obligación** es explícita al inscribir (por defecto, hoy + `DefaultDueDays` del gimnasio, 7). Con `AllowUntilOverdue` el acceso se corta cuando la obligación con saldo supera esa fecha.
- Una membresía pasa de `PendingPayment` a `Active` al cumplirse la regla de activación de la política: con `AllowUntilOverdue`, al aplicarse el primer pago confirmado; con `RequireFullPayment`, al saldar la obligación.
- Un alumno pertenece a la **sucursal donde se inscribió**. Quien solo accede a ciertas sucursales ve y edita únicamente los alumnos de esas sucursales (otro alumno responde 404).
- Los pagos en **efectivo** exigen una sesión de caja abierta. Las **transferencias** quedan `PendingVerification` hasta que alguien con permiso las confirme (ADR 0005). La tarjeta se registra como voucher del datáfono (referencia obligatoria); no hay pasarela.
- La **fotografía** se guarda como URL opcional; la carga de archivos queda fuera de esta fase.
- Sin descuentos manuales: los descuentos salen de promociones válidas, con un tope máximo configurable por gimnasio.

## Esquemas y tablas (un `DbContext`, `CommercialDbContext`)
| Esquema | Tablas |
|---|---|
| `members` | `members`, `membership_plans`, `plan_branches`, `promotions`, `promotion_plans`, `memberships`, `membership_events`, `commercial_settings`, `counters` |
| `billing` | `tax_rates` (global), `payment_obligations`, `payments`, `payment_allocations`, `idempotency_keys` |
| `cash` | `cash_registers`, `cash_sessions`, `cash_movements` |
| `access` | `attendance` |

Todas las tablas de tenant llevan `tenant_id`, filtro global de EF, **RLS forzado** y claves foráneas compuestas `(tenant_id, id)`. `tenant_id` y sucursal referencian a `organizations` con FK compuesta `(tenant_id, branch_id)`.

## Reglas financieras
- Importes `numeric(12,2)`; `Money` redondea a 2 decimales, hacia fuera del cero.
- **Un solo origen del saldo:** saldo = importe exigible − suma de asignaciones de pagos *confirmados*. No hay columna de saldo. El estado `Pending/PartiallyPaid/Paid/Voided` se actualiza en la misma transacción que el pago; `Overdue` se deriva (vencimiento < hoy y saldo > 0) y no se almacena.
- **Concurrencia:** cada pago bloquea sus obligaciones con `SELECT … FOR UPDATE` (en orden de id, sin interbloqueos) y verifica en esa transacción que lo confirmado + reservado no supere lo exigible.
- **Idempotencia:** los endpoints críticos (`POST /payments`, `/payments/{id}/reverse`, abrir/cerrar caja) exigen `Idempotency-Key`; repetir la clave con el mismo cuerpo devuelve la respuesta original; con otro cuerpo, 422.
- Los pagos son inmutables. Se corrigen con un **reverso** que crea un movimiento inverso (y, en efectivo, un egreso en la sesión de caja abierta).
- Saldo de caja ≠ saldo de obligación: la caja suma solo movimientos de efectivo de su sesión.
- Nada se borra físicamente si tiene movimientos: se desactiva, anula o revierte.

## Permisos nuevos
`members.read|create|update|status`, `plans.read|manage`, `promotions.read|manage`, `memberships.read|create|renew|freeze|cancel`, `obligations.read|create|void`, `payments.read|create|confirm|reverse`, `cash.read|manage|operate`, `attendance.read|register|override`, `commercial.settings.manage` y, solo para plataforma, `platform.taxes.manage`. El Propietario los recibe todos al arrancar. Las plantillas de rol se actualizan solo para gimnasios nuevos; los existentes ajustan sus roles desde la pantalla de roles.

## Notificaciones de WhatsApp (decidido el 2026-10-27)
- **Quién y cuándo:** a **3 días** del vencimiento, el administrador y la recepción de la sucursal reciben un aviso con las membresías por vencer. Al **alumno** se le envía un mensaje cordial **el día del vencimiento** y **3 días después** si no renovó, solo si aceptó recibir mensajes. Un mensaje ya enviado no se repite.
- **Proveedor:** WhatsApp Business Cloud API de Meta detrás de un puerto (`IWhatsAppSender`), como la pasarela de tarjeta: queda **en espera** hasta contar con la cuenta verificada, el número y las plantillas aprobadas por Meta. Mientras tanto el sistema calcula y **registra** los avisos pendientes sin enviarlos.
- **Diseño:** una tarea del Worker revisa cada día las membresías por vencer y escribe en `notifications` (tipo, destinatario, membresía, fecha programada, estado, intentos, error) con una restricción única `(membresía, tipo, destinatario)` que impide duplicados aunque el Worker corra dos veces. Los textos viven en plantillas con idioma (es por defecto) y datos mínimos: el mensaje al alumno no incluye saldos ni datos sensibles.
- **Consentimiento (LOPDP):** el alumno guarda su aceptación de recibir WhatsApp (fecha y quién la registró); sin ella nunca se le escribe, y puede retirarla. El personal recibe avisos según su rol y número registrado.
- **Reintentos y fallos:** reintentos con espera creciente; los fallos definitivos quedan visibles para el administrador.
## Bloques
| # | Bloque | Estado |
|---|---|---|
| 1 | Módulo, `DbContext`, RLS, migración, permisos | Hecho |
| 2 | Alumnos | Hecho |
| 3 | Planes, promociones, tarifas de IVA y políticas del gimnasio | Hecho |
| 4 | Membresías | Pendiente |
| 4b | Notificaciones de WhatsApp (vencimientos) | Pendiente, después de las membresías |
| 5 | Obligaciones, pagos y abonos | Pendiente |
| 6 | Caja | Pendiente |
| 7 | Asistencia | Pendiente |
| 8 | Angular | Pendiente |
| 9 | Pruebas de regresión y documentación | Pendiente |
