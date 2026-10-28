# ADR 0011 — Módulo Analytics (dashboards, reportes e inteligencia de negocio)

**Estado:** Decisiones confirmadas el 2026-10-28 (Fase 6 en nuestra numeración; «FASE 5» del prompt del usuario). Se implementa por bloques; cada bloque marca aquí lo realmente hecho.

## Decisiones confirmadas
1. **Módulo nuevo `FitCore.Analytics`, solo de lectura**, con permisos propios. Lee Commercial y Classes por vistas `*Ref` (mismo patrón que `Classes`; lo vigila la prueba de arquitectura). **No tiene tablas de negocio**: los indicadores se calculan al consultar, con agregaciones en PostgreSQL. Única tabla propia: `analytics.report_exports` (historial de exportaciones).
2. **Exportaciones: CSV síncrono guardado con vencimiento.** `POST /reports/exports` genera el archivo al instante (tope 20.000 filas), lo guarda en el almacén privado (`IFileStore`) con vencimiento de 24 h y registra quién lo pidió; el estado y la descarga se consultan por id con permisos. Sin trabajos en segundo plano. **PDF queda pendiente** (no hay biblioteca instalada; añadirla exige decidir una licencia).
3. **«Alumno activo»** = alumno con ficha Activa que tiene una membresía **vigente al último día del período** (definición abajo), no solo el estado administrativo.
4. **Sin caché, sin tablas de resumen, sin vistas materializadas.** Se mide primero (bloque 10); si una consulta lo justifica se añade un índice, y solo entonces se discute otra cosa.
5. **Gráficos propios en SVG** (como en la fase de bienestar): no hay librería de gráficos instalada y no se añade ninguna.
6. **Fuera de la fase:** facturación SRI, nómina, contabilidad integral. Tampoco se llama «utilidad» ni «ganancia» a lo cobrado: no hay costos ni gastos.

## Diagnóstico del repositorio (bloque 1)
| Necesidad | Situación real | Acción |
|---|---|---|
| Moneda | Una sola: `Payment.Currency = "USD"` (también en obligaciones) | Todo se informa en USD; no se suman monedas |
| Cobros | `billing.payments` (importe único por pago, `paid_at`, `status` Confirmed / PendingVerification / Reversed, `reversed_at`, método Cash / BankTransfer / Card / Other, sucursal, `cash_session_id`) | Se suma `payments.amount`, **nunca** uniendo asignaciones (evita el doble conteo). Único índice útil ya existente: `(tenant, branch, paid_at)` |
| Abonos y asignaciones | `billing.payment_allocations` (pago → obligación, importe); único `(pago, obligación)`; Σ asignaciones = importe del pago | Solo se usan para atribuir un cobro a un plan |
| Saldos | `Ledger.BalancesAsync` es **la** fuente: Σ asignaciones de pagos Confirmed (cobrado) y PendingVerification (reservado); Reversed no cuenta. El saldo no se guarda | Analytics reproduce la misma regla en SQL (misma definición, probada con casos cruzados contra el `Ledger`) |
| Obligaciones | `billing.payment_obligations`: `issue_date`, `due_date` (fechas locales), `payable_amount`, estado Pending / PartiallyPaid / Paid / Voided; «Vencida» se **deriva** (`due_date < hoy` y no pagada) | Mismas reglas; `ReceivablesHandlers` ya calcula cartera y antigüedad: se contrasta |
| Membresías | `members.memberships` con copia de plan/precio, `start_date`, `end_date`, estado guardado Active / PendingPayment / Frozen / Cancelled; «Vencida» se **deriva** de `end_date`; `renewed_from_id`; historial en `membership_events` (Created, Activated, Frozen, Reactivated, Cancelled, Renewed) | Se usan eventos para saber si estaba vigente en una fecha pasada |
| Caja | `cash.cash_sessions` (apertura, `counted_amount`, `expected_at_close`, `difference`), `cash_movements` (tipo, dirección, importe) | Se reportan tal cual se guardaron al cerrar |
| Asistencia al gimnasio | `access.attendance` (`check_in_at`, método, excepción) | Visitas = entradas |
| Clases | `classes.class_sessions` (estado Scheduled / Cancelled / Completed, capacidad), `class_reservations` (Confirmed / Waitlisted / Offered / Cancelled / Expired, cancelación tardía), `class_attendance` (Attended / NoShow) | Ocupación y participación según las reglas de abajo |
| Zona horaria | `organizations.tenants.time_zone` y `branches.time_zone` | Ver «Fechas y zona horaria» |
| Reportes existentes | `GET /receivables/summary` (cartera, antigüedad, deudores, por sucursal) y listados paginados | Se reutiliza la definición; no se duplican endpoints |
| Almacén de archivos | `IFileStore` (disco privado, claves aleatorias) de la fase de bienestar | Guarda los CSV |
| Permisos | `payments.read`, `cash.read`, `attendance.read`, `members.read`… existen pero son de operación, no de analítica | Permisos nuevos (abajo) |

## Fechas y zona horaria
- Un filtro de fechas es un par de **fechas locales** `desde`–`hasta`, ambas inclusive. Se interpretan en la zona horaria del **gimnasio** (`tenants.time_zone`); si el reporte se filtra a **una sola sucursal**, en la zona de esa sucursal.
- Para las columnas que guardan instantes (`paid_at`, `reversed_at`, `check_in_at`, `starts_at`, `created_at`): rango `[desde 00:00 local → UTC, (hasta + 1 día) 00:00 local → UTC)`, medio abierto.
- Las columnas de fecha local (`issue_date`, `due_date`, `start_date`, `end_date`, `enrolled_on`) se comparan directamente con las fechas, sin conversión.
- Presets: Hoy, Últimos 7 días (incluye hoy), Últimos 30 días, Mes actual, Mes anterior, Año actual, Rango personalizado: todos se resuelven en el servidor con la fecha local de hoy.
- **Período comparable:** misma cantidad de días inmediatamente anterior; para «Mes actual» (parcial) se compara con el mismo número de días del mes anterior; «Mes anterior» y «Año actual» se comparan con su equivalente. Se devuelve siempre el rango comparado.
- Límites: rango máximo 366 días en detalle y 24 meses en series mensuales.

## Reglas financieras (qué fecha usa cada cosa)
| Concepto | Fórmula | Fecha |
|---|---|---|
| **Cobrado** | Σ `payments.amount` con estado **Confirmed** | `paid_at` |
| Por verificar | Σ `amount` en **PendingVerification** (transferencias aún no confirmadas). No es cobrado | `paid_at` |
| **Revertido** | Σ `amount` en **Reversed** | `reversed_at` (informe de reversiones muestra también `paid_at`) |
| Por método | Cobrado agrupado por `method` (`Other` con su detalle) | `paid_at` |
| **Obligaciones generadas** | Σ `payable_amount` con estado ≠ Voided | `issue_date` |
| **Anuladas** | Σ `payable_amount` con estado Voided (informe histórico) | `voided_at` |
| **Saldo pendiente** | Σ (`payable_amount` − cobrado de la obligación) de Pending / PartiallyPaid. Es una **foto de hoy**, no se reconstruye hacia atrás | hoy |
| **Vencido** | Saldo pendiente con `due_date` < hoy | `due_date` |
| Ingreso por plan | Σ asignaciones de pagos Confirmed cuya obligación pertenece a una membresía de ese plan; sin membresía = «Cobros sin plan» | `paid_at` |
| Ingreso por sucursal | Cobrado agrupado por `payments.branch_id` | `paid_at` |
| Caja | Aperturas, cierres, esperado vs contado vs diferencia **tal como se guardó al cerrar**; movimientos | `opened_at` / `closed_at` / `created_at` |

Un pago cobrado en octubre y revertido en noviembre: el cobrado vigente de octubre, consultado en noviembre, **ya no lo incluye** (es un estado actual); el informe de reversiones sí lo muestra con ambas fechas. Esto se documenta como limitación de «foto actual».

## Alumnos y membresías
- **Membresía vigente en la fecha D**: tiene evento `Activated` con fecha ≤ D (cobrada/activada), `start_date` ≤ D ≤ `end_date`, y no fue cancelada antes de D (`cancelled_at` nulo o posterior). Congelada cuenta como vigente (el alumno conserva su membresía, no entra).
- **Alumno activo en D**: ficha Activa con al menos una membresía vigente en D.
- **Nuevo alumno**: `enrolled_on` en el período. **Inscripción nueva**: membresía creada en el período sin membresía previa del alumno. **Renovación**: `renewed_from_id` no nulo. **Reincorporación**: membresía creada sin `renewed_from_id` pero con alguna membresía previa del alumno.
- **Vencimientos del período**: membresías con `end_date` en el período, no canceladas. **Próximos a vencer**: `end_date` entre hoy y hoy + N días (7, 15 o 30), vigentes.
- **Tasa de renovación** (cohorte = las membresías que vencen en el período): renovadas / vencidas, donde «renovada» = existe una membresía con `renewed_from_id` igual a ella. Mientras el período es reciente subestima (el alumno puede renovar después); la respuesta indica la fecha de cálculo.
- **Retención** (cohorte = alumnos activos el **último día del período anterior**): retenidos = los de esa cohorte que siguen siendo activos el último día del período actual. `retención = retenidos / cohorte`; `bajas = cohorte − retenidos`. Si la cohorte es 0 → «Sin base de comparación».
- **Baja** no es una membresía cancelada: un alumno es baja del período solo si estaba en la cohorte y **no** tiene ninguna membresía vigente al cierre (si canceló una y compró otra, sigue retenido). Las bajas se informan como «sin membresía vigente», no como «se fue para siempre».
- **Altas** de la tendencia = nuevos alumnos + reincorporaciones; **bajas** = según lo anterior, por mes.

## Operación y clases
- **Asistencia al gimnasio** = entradas (`access.attendance`) por día / semana / mes y sucursal; frecuencia = visitas ÷ alumnos activos; por alumno solo con permiso (código y nombre, sin documento ni contacto).
- **Sesión programada** (estado Scheduled y aún por ocurrir), **realizada** (Completed), **cancelada** (Cancelled), y **pendiente de cierre** (Scheduled cuya hora ya pasó: el sistema no las cierra solo, se informan aparte, nunca como realizadas).
- **Participantes válidos** = reservas **Confirmed**. No cuentan: Waitlisted, Offered (cupo apartado, no ocupación real), Cancelled ni Expired.
- **Ocupación reservada** = Confirmed ÷ capacidad de la sesión. **Ocupación asistida** = Attended ÷ capacidad (solo sesiones Completed con asistencia registrada). Se excluyen las sesiones canceladas y las de capacidad 0; si faltan datos se devuelve «Sin datos», nunca 0 % engañoso. **Ocupación media** = Σ ocupados ÷ Σ capacidad (ponderada), no el promedio de porcentajes.
- Inasistencias = `class_attendance` NoShow. Cancelaciones de reserva con indicador de cancelación tardía. Demanda por actividad y por día/hora (hora local).
- **Entrenador:** solo sesiones donde es el instructor y solo sus alumnos autorizados.

## Variaciones (periodo comparable)
`variación % = (actual − anterior) ÷ anterior`. Si `anterior = 0` → estado `NoBaseline` («Sin base de comparación»), sin porcentaje. Se devuelve siempre `actual`, `anterior`, `variación absoluta` y el estado. Las razones (retención, ocupación) comparan en puntos porcentuales.

## Permisos nuevos (se validan en el servidor)
| Permiso | Para qué |
|---|---|
| `analytics.dashboard.read` | Entrar al dashboard; cada sección se entrega solo si el usuario tiene también el permiso del área (si no, la sección viene omitida con motivo, no vacía) |
| `reports.financial.read` | Cobros, métodos, saldos, caja, reversos |
| `reports.members.read` | Alumnos, membresías, retención, renovaciones, vencimientos |
| `reports.operations.read` | Asistencia al gimnasio y clases de todas las sucursales autorizadas |
| `reports.instructor.read` | Solo sus propias sesiones y su asistencia (entrenador) |
| `reports.compare` | Comparación entre sucursales |
| `reports.export` | Solicitar y descargar exportaciones (además del permiso del reporte exportado) |

Reparto inicial: Propietario y AdministradorSucursal todos (este último solo su sucursal); Contador: financiero, alumnos, comparación y exportar (todas las sucursales); Cajero: dashboard + financiero de su sucursal; Recepción: dashboard + operación; Entrenador: dashboard + instructor; Nutricionista y Alumno: ninguno. Los datos de salud (bienestar) **no** entran en analítica.

## Privacidad
Los agregados no llevan datos personales. Los listados por alumno (saldos, asistencia) muestran **código y nombre**, nunca documento, teléfono ni correo. Los nombres de archivo son genéricos (`reporte-cobros-AAAAMMDD-xxxx.csv`) y los logs registran quién, cuándo y qué reporte, no su contenido. Texto controlado por usuarios en un CSV (nombres, conceptos, motivos) se neutraliza contra fórmulas (prefijo `'` si empieza por `=`, `+`, `-`, `@`, tab o retorno).

## Rendimiento (criterio)
Proyección de las columnas necesarias, agregaciones en PostgreSQL, sin N+1, filtro de tenant por RLS y por consulta, paginación en listados. Índices **solo si el plan de ejecución lo justifica** con volumen realista; los candidatos a evaluar son `memberships (tenant, created_at)`, `memberships (tenant, end_date)`, `payments (tenant, status, paid_at)` y `class_sessions (tenant, branch, starts_at)`. Cualquier índice nuevo entra con su migración en el módulo dueño de la tabla.

## Plan de bloques
| Bloque | Contenido | Estado |
|---|---|---|
| 1 | Diagnóstico, inventario de indicadores y este ADR | Hecho |
| 2 | Módulo `Analytics`: esqueleto, vistas `*Ref`, permisos, período/zona horaria, variaciones y `GET /analytics/context` | Hecho |
| 3 | Dashboard ejecutivo (`/analytics/dashboard`, `/analytics/dashboard/comparison`) y por perfil | Hecho |
| 4 | Reportes financieros (cobros, medios de pago, saldos, caja, reversiones) | Hecho |
| 5 | Reportes de alumnos y membresías (resumen, altas y bajas, retención, renovación, vencimientos) | Hecho |
| 6 | Reportes operativos y de clases (asistencia al gimnasio, ocupación, demanda, cancelaciones) | Hecho |
| 7 | Comparación entre sucursales | Pendiente |
| 8 | Exportaciones CSV (migración `report_exports`, vencimiento, descarga protegida) | Pendiente |
| 9 | Angular: dashboard, reportes, comparación, exportaciones | Pendiente |
| 10 | Rendimiento medido, regresión, documentación e informe de la fase | Pendiente |

## Dependencias y límites ya conocidos
- **PDF:** requiere una biblioteca (p. ej. QuestPDF) y decisión de licencia; diferido.
- **Saldos históricos:** el sistema no guarda el saldo de una fecha pasada; los saldos son «de hoy».
- **Sesiones de clase no cerradas** se informan como «pendientes de cierre».
- Sin costos ni gastos no existe utilidad: solo cobrado, generado y pendiente.
