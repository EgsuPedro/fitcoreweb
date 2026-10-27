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
## Reglas de membresías (bloque 4, implementadas)
- **Una sola transacción:** inscribir y renovar crean la membresía y su obligación juntas (o ninguna). Consumir un uso de la promoción también va en esa transacción, con un incremento atómico en la base.
- **Términos copiados:** la membresía y la obligación guardan precio, descuento, duración, tipo y tarifa de IVA del momento. Editar el plan o cambiar el IVA por decreto no las toca.
- **Estado pendiente → activo:** nace `PendingPayment`. Pasa a `Active` con el primer pago (bloque 5). `Expired` no se guarda: se deriva de la fecha de fin; nadie tiene que "vencer" membresías con un proceso.
- **Una a la vez:** el solapamiento se valida en la aplicación y lo garantiza la base con una restricción de exclusión (`daterange` por alumno entre membresías pendientes, activas o congeladas). Dos solicitudes simultáneas no pueden dejar dos vigentes.
- **Renovar:** crea otra membresía que empieza al terminar la actual (o hoy si ya venció). No se renueva una pendiente de pago, congelada, cancelada ni ya renovada; la original solo recibe un evento "Renovada" en su historial.
- **Congelar:** solo membresías activas, de planes que lo permitan y con cupo. Al reactivar, el fin se alarga por los días congelados sin pasar del cupo; el historial muestra fecha anterior, nueva, días congelados y días otorgados. No se congela si ya hay una renovación programada.
- **Cancelar:** exige motivo. Anula la obligación solo si no tiene ningún pago (cuando existan pagos, el bloque 5 los corrige con reversos). Libera el periodo.
- **Fechas** en la zona horaria de la sucursal (Galápagos tiene una hora menos que el continente).
## Reglas de cobros (bloque 5, implementadas)
- **Saldo = exigible − cobrado.** No hay columna de saldo: sale de las asignaciones de pagos *confirmados*. Las transferencias *pendientes* reservan saldo (nadie más puede cobrar ese monto) pero no lo pagan hasta que se confirman. Un pago revertido no cuenta en ninguno.
- **Estado de la obligación:** `Pending → PartiallyPaid → Paid` se recalcula en la misma transacción del pago o del reverso; `Overdue` se deriva (vence antes de hoy con saldo) y no se guarda.
- **Concurrencia:** cada pago bloquea sus obligaciones con `SELECT … FOR UPDATE` (en orden de id) antes de calcular el disponible. Diez abonos simultáneos de 10,00 sobre 34,50: entran exactamente tres. Un trigger en la base es el respaldo: las asignaciones de pagos no revertidos nunca superan lo exigible.
- **Idempotencia:** `POST /payments`, `/payments/{id}/confirm` y `/payments/{id}/reverse` exigen `Idempotency-Key` (hasta 100 caracteres). Repetir la clave con el mismo contenido y usuario devuelve la respuesta original sin duplicar; con otro contenido u otro usuario, 422. Ocho solicitudes simultáneas con la misma clave producen un solo pago.
- **Importe y asignaciones:** el cliente nunca envía un saldo ni "total pagado": el backend verifica. Sin asignaciones, el pago cubre las obligaciones abiertas del alumno de la más antigua a la más nueva. No hay saldo a favor: el importe no puede superar lo pendiente.
- **Medios:** tarjeta y otros medios se confirman al registrarse (tarjeta: referencia de voucher obligatoria; otros: nombre del medio). Transferencia: referencia obligatoria y queda pendiente hasta que alguien con `payments.confirm` la confirme. Efectivo: ver "Reglas de caja". Nunca se guarda un número de tarjeta: una referencia que parece uno (13–19 dígitos que cumplen Luhn) se rechaza.
- **Inmutabilidad:** un pago no se modifica ni se borra. Un reverso cambia su estado, exige motivo y deja responsable y fecha. La base lo garantiza: un trigger impide cambiar importe, medio, alumno o referencia, y reactivar un reverso; el rol de la aplicación no puede borrar pagos ni asignaciones, ni modificar o borrar el historial de membresías.
- **Activación:** un pago confirmado activa la membresía según la política (`AllowUntilOverdue`: primer pago; `AllowWithMinimumPayment`: % mínimo; `RequireFullPayment`: saldada). Un reverso posterior **no** la desactiva: el acceso se decide con los saldos vigentes en el bloque de asistencia.
- **Anular obligaciones:** `POST /payment-obligations/{id}/void` con motivo; no se puede si tiene pagos vigentes (primero se revierten). Cancelar una membresía anula su obligación solo si no tiene pagos; si los tiene, la conserva.
- **Obligaciones manuales** (`POST /payment-obligations`): otros conceptos, con el IVA vigente desglosado y congelado.
- **Recibo interno** (`GET /payments/{id}/receipt`): número `RC-000001` por gimnasio, líneas, total y quién lo recibió; aclara que no es factura tributaria.
- **Historial de cambios** de un pago: queda en la auditoría (`Payment.Created/Updated`, `PaymentAllocation.Created`, `PaymentObligation.Updated`), sin datos de tarjeta.
## Reglas de caja y cartera (bloque 6, implementadas)
- **Sesiones:** una caja se abre con un saldo inicial y se cierra con un arqueo. Una caja admite una sola sesión abierta (configurable) y cada usuario solo puede tener una abierta. La aplicación lo valida y dos índices únicos parciales deciden cuando dos aperturas llegan a la vez.
- **Saldo esperado = apertura + ingresos − egresos**, siempre calculado a partir de los movimientos. El cierre recibe solo el saldo *contado*: cualquier "esperado" que envíe el cliente se ignora. Si hay diferencia, la observación es obligatoria; se guarda con su signo (faltante o sobrante) y el esperado del momento del cierre.
- **Efectivo:** un cobro en efectivo entra a la sesión abierta *del propio cajero* en la sucursal del alumno (la caja que abrió otra persona, o la de otra sucursal, no sirve). El pago, su asignación, el movimiento de caja y la actualización de la obligación van en una sola transacción.
- **Reverso de un cobro en efectivo:** si la sesión original sigue abierta, el egreso se registra ahí (el efectivo nunca salió del cajón); si ya se cerró, sale de la caja abierta de quien revierte y exige efectivo suficiente. Nada de esto modifica una sesión cerrada.
- **Movimientos manuales** (`ManualIn`/`ManualOut`): exigen motivo, `Idempotency-Key` y, si son egresos, efectivo suficiente (la caja nunca queda en negativo). Los cobros y reversos de efectivo no se pueden inventar a mano.
- **Cierre vs. cobros:** cobros, reversos, movimientos y cierre bloquean la fila de la sesión (`FOR UPDATE`, siempre después de las obligaciones): mientras se cierra no entra dinero, y nada se registra después del cierre. Un trigger y la restricción de estado son el respaldo en la base.
- **Quién cierra:** quien la abrió o un supervisor con `cash.manage` (indicando la sesión).
- **Solo-anexado:** los movimientos no se modifican ni se borran (trigger y permisos); una sesión cerrada es definitiva; las sesiones y cajas no se borran. Las migraciones reaplican siempre estas restricciones para que una migración futura no las deshaga.
- **Cartera** (`GET /receivables/summary`): lo que se debe hoy (exigible − cobrado), por antigüedad de vencimiento (al día, 1-30, 31-60, 61-90, más de 90), por sucursal y los principales deudores con su teléfono. Excluye obligaciones saldadas o anuladas; una transferencia pendiente de verificar sigue contando como deuda.
## Reglas de asistencia (bloque 7, implementadas)
- **Hora del servidor:** la entrada y la salida siempre llevan la hora del servidor; cualquier hora que envíe el cliente se ignora.
- **Evaluación de acceso** (en cada entrada, con los datos de hoy en la zona horaria de la sucursal): alumno activo → membresía `Active` cuyas fechas cubren hoy (el último día todavía entra) → el plan permite esa sucursal → política de acceso con deuda según los saldos vigentes. Motivos de rechazo con código estable: `member_inactive`, `no_active_membership`, `membership_pending_payment`, `membership_expired`, `membership_not_started`, `membership_frozen`, `plan_branch_restricted`, `debt_pending`, `debt_overdue`, `minimum_payment_not_met`.
- **Deuda en cada entrada:** `RequireFullPayment` exige saldo cero; `AllowUntilOverdue` rechaza si una obligación de la membresía venció con saldo; `AllowWithMinimumPayment` exige el % mínimo del total. Como se evalúa con los saldos vigentes, un reverso de pago corta el acceso aunque la membresía siga activa.
- **Entradas duplicadas:** una entrada abierta impide otra del mismo alumno, en esa u otra sucursal (`already_checked_in`, `open_entry_elsewhere`); un índice único parcial lo garantiza ante solicitudes simultáneas. Una entrada olvidada se cierra con una salida registrada por el personal.
- **Excepciones:** si la regla rechaza y el motivo es "excepcionable", quien tiene `attendance.override` puede autorizar el acceso indicando un motivo (mínimo 5 caracteres). Queda marcada como excepción, con motivo y responsable, y auditada. No aplica a alumnos inactivos ni a una entrada ya abierta; quien no tiene el permiso recibe `override_not_allowed`.
- **Alumnos de otra sucursal:** el alumno se busca en todo el gimnasio, porque puede entrenar en otra sucursal si su plan lo permite (plan sin sucursales = todas). La visita se registra en la sucursal donde ocurre y el usuario solo puede registrar en las suyas.
- **QR:** `POST /members/{id}/qr-token` emite un token aleatorio y opaco (`fc1_` + 43 caracteres) que se muestra una sola vez; en la base solo se guarda su SHA-256 (y la auditoría lo redacta). No contiene datos del alumno ni del gimnasio; emitir otro invalida el anterior. La validación es siempre en el backend y exige a un usuario del personal con `attendance.register`: ningún cliente móvil decide el resultado.
- **Dispositivos físicos:** el método `Device` queda reservado en el modelo; todavía no hay integración.
- **Visitas solo-anexado:** una visita solo cambia al registrar su salida; después es definitiva, y nunca se borran (trigger y permisos de la base).
- **Pendiente (no incluido):** cierre automático de entradas abiertas olvidadas y registro de los intentos rechazados.
## Bloques
| # | Bloque | Estado |
|---|---|---|
| 1 | Módulo, `DbContext`, RLS, migración, permisos | Hecho |
| 2 | Alumnos | Hecho |
| 3 | Planes, promociones, tarifas de IVA y políticas del gimnasio | Hecho |
| 4 | Membresías (incluye la obligación de pago en lectura) | Hecho |
| 4b | Notificaciones de WhatsApp (vencimientos) | Pendiente, después de las membresías |
| 5 | Obligaciones, pagos y abonos (efectivo se habilita con la caja) | Hecho |
| 6 | Caja y cartera (incluye el efectivo) | Hecho |
| 7 | Asistencia (entrada/salida, QR, excepciones) | Hecho |
| 8 | Angular | Pendiente |
| 9 | Pruebas de regresión y documentación | Pendiente |
