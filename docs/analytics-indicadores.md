# Indicadores de analítica

Catálogo de los 46 indicadores que calcula el backend (módulo `FitCore.Analytics`). El texto es el mismo que ve el usuario en «Qué mide este indicador» (`public/i18n/es.json`, `analytics.kpi.<clave>`); las reglas completas de fechas, zonas horarias y alcance están en el [ADR 0011](adr/0011-modulo-analytics.md).

**Convenciones comunes.** Todo importe está en USD. Un indicador trae nombre, definición, valor, unidad, período, fecha usada y variación frente al período comparable. Si el valor anterior es 0, la variación es «Sin base de comparación» (nunca un porcentaje); las razones (ocupación, tasas) varían en **puntos porcentuales**. Las «fotos de hoy» (saldo, vencido, inactivos, próximos a vencer) no se comparan. Lo cobrado **no** es utilidad: no hay costos ni gastos en el sistema.

## Finanzas (`reports.financial.read`)

| Clave | Indicador | Definición |
|---|---|---|
| `collected` | Total cobrado | Pagos confirmados recibidos en el período (el importe de cada pago, sin contar dos veces abonos ni asignaciones). No es utilidad: no descuenta costos ni gastos. Fecha usada: fecha de pago. |
| `collected_cash` | Cobrado en efectivo | Parte del total cobrado que se recibió en efectivo. Fecha usada: fecha de pago. |
| `collected_other` | Cobrado por otros medios | Parte del total cobrado por transferencia, tarjeta u otros medios. Fecha usada: fecha de pago. |
| `pending_verification` | Transferencias por verificar | Pagos registrados que aún no se confirman; no cuentan como cobrado. Fecha usada: fecha de pago. |
| `issued` | Obligaciones generadas | Importe exigible de las obligaciones emitidas en el período, sin las anuladas. Es lo facturado al alumno, no lo cobrado. Fecha usada: fecha de emisión. |
| `outstanding` | Saldo pendiente | Lo que los alumnos deben hoy: exigible menos cobrado de las obligaciones abiertas. Es una foto de hoy; el sistema no guarda el saldo de una fecha pasada. |
| `overdue` | Saldo vencido | Parte del saldo pendiente cuya fecha de vencimiento ya pasó. |
| `reversed` | Pagos revertidos | Importe de los pagos revertidos en el período; no está incluido en el total cobrado. Fecha usada: fecha de reversión. |
| `payments_count` | Pagos recibidos | Cantidad de pagos confirmados en el período. |
| `average_payment` | Pago promedio | Total cobrado ÷ pagos recibidos. |
| `partial_amount` | Pagos parciales (abonos) | Parte cobrada en asignaciones que cubren solo una parte del importe exigible de la obligación. |
| `partial_count` | Pagos que abonan | Pagos con al menos una asignación que no salda el exigible de su obligación. |

## Alumnos y membresías (`reports.members.read`)

| Clave | Indicador | Definición |
|---|---|---|
| `active_members` | Alumnos activos | Alumnos con ficha activa y una membresía vigente y cobrada el último día del período. |
| `new_members` | Alumnos nuevos | Fichas de alumno creadas en el período. Fecha usada: fecha de alta. |
| `new_enrollments` | Nuevas inscripciones | Membresías vendidas en el período como primer contrato de un alumno. Fecha usada: fecha de venta. |
| `renewals` | Membresías renovadas | Membresías vendidas en el período como renovación de otra. Fecha usada: fecha de venta. |
| `reincorporations` | Reincorporaciones | Membresías vendidas a alumnos que ya tuvieron otras, sin ser renovación. |
| `gains` | Altas | Inscripciones nuevas + reincorporaciones. |
| `losses` | Bajas | Alumnos activos el día anterior al período que ya no tienen membresía vigente al terminarlo. Una membresía cancelada no es una baja si el alumno tiene otra vigente. |
| `expired` | Membresías vencidas | Membresías activas cuyo fin cayó dentro del período y ya pasó (no incluye canceladas). Fecha usada: fecha de fin. |
| `expiring_soon` | Próximas a vencer | Membresías activas que terminan en los próximos días elegidos. Foto de hoy. |
| `inactive_members` | Alumnos inactivos | Alumnos con la ficha marcada como inactiva. Foto de hoy. |
| `members_with_balance` | Alumnos con saldo pendiente | Alumnos con alguna obligación abierta que aún se debe. Foto de hoy. |

## Operación y clases (`reports.operations.read`)

| Clave | Indicador | Definición |
|---|---|---|
| `gym_visits` | Asistencia al gimnasio | Entradas al gimnasio registradas en el período. Fecha usada: fecha y hora de entrada. |
| `visits` | Visitas | Entradas al gimnasio registradas en el período. |
| `unique_visitors` | Alumnos que asistieron | Alumnos distintos con al menos una entrada en el período. |
| `frequency` | Frecuencia de asistencia | Visitas ÷ alumnos distintos que asistieron (promedio de visitas por asistente). |
| `sessions_held` | Clases realizadas | Sesiones cerradas como realizadas. Las ya pasadas que nadie cerró se informan aparte como pendientes de cierre. Fecha usada: inicio de la sesión. |
| `sessions_cancelled` | Clases canceladas | Sesiones canceladas cuyo inicio caía en el período. |
| `class_occupancy` | Ocupación de clases | Reservas confirmadas ÷ capacidad de las sesiones no canceladas (ponderada). No cuenta lista de espera, cupos ofrecidos ni reservas canceladas. |
| `occupancy_reserved` | Ocupación reservada | Reservas confirmadas ÷ capacidad de las sesiones no canceladas (ponderada). No cuenta lista de espera, cupos ofrecidos ni reservas canceladas. |
| `occupancy_attended` | Ocupación asistida | Asistentes ÷ capacidad de las sesiones realizadas con asistencia registrada. Sin registros no hay dato. |
| `waitlisted` | Demanda en espera | Personas en lista de espera o con un cupo ofrecido (querían entrar y no había lugar). No es ocupación. |
| `reservations_confirmed` | Reservas confirmadas | Reservas confirmadas de las sesiones no canceladas del período. |
| `reservations_cancelled` | Reservas canceladas | Reservas canceladas por el alumno o el personal (no las que se cancelaron al cancelar la sesión). |
| `class_no_shows` | Inasistencias a clases | Reservas confirmadas marcadas como «no asistió». |
| `no_shows` | Inasistencias | Reservas confirmadas marcadas como «no asistió». |
| `attendance_rate` | Tasa de asistencia | Asistió ÷ (asistió + no asistió) entre lo registrado. |
| `late_cancellations` | Cancelaciones fuera de plazo | Reservas canceladas después del plazo de cancelación de la clase. |
| `cancellation_rate` | Tasa de cancelación de reservas | Canceladas ÷ (confirmadas + canceladas) de las sesiones no canceladas. |
| `offers_expired` | Cupos ofrecidos que vencieron | Invitaciones de la lista de espera que no se aceptaron a tiempo. |

## Instructor, solo sus sesiones (`reports.instructor.read`)

| Clave | Indicador | Definición |
|---|---|---|
| `my_sessions_held` | Mis clases realizadas | Sus sesiones cerradas como realizadas. |
| `my_sessions_cancelled` | Mis clases canceladas | Sus sesiones canceladas cuyo inicio caía en el período. |
| `my_occupancy` | Ocupación de mis clases | Reservas confirmadas ÷ capacidad de sus sesiones no canceladas. |
| `my_attended` | Asistencias a mis clases | Participantes que asistieron a sus clases. |
| `my_no_shows` | Inasistencias a mis clases | Participantes confirmados que no asistieron a sus clases. |
