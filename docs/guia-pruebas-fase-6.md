# Guía de validación manual — Fase 6 (analítica): bloques 2 a 8

Hasta el bloque 8 la fase entrega **API** (contexto, dashboard, reportes financieros, de alumnos y membresías, operativos y de clases, comparación de sucursales y exportaciones a CSV); las pantallas de Angular llegan en el bloque 9. Esta guía permite validar todo lo hecho **sin pantallas**, con datos reales y con los usuarios de cada rol. Se irá ampliando con cada bloque.

## 0. Preparación

1. El entorno de siempre debe estar levantado con el código actual (API y Worker reconstruidos):
   ```powershell
   docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env up --build -d
   ```
2. Gimnasio de práctica **`analitica`** (limpio, solo para esta validación), clave **`DemoFit2026x`**, con un usuario por rol (`propietario@analitica.fitcore.test`, `admin.uio@…`, `recepcion.uio@…`, `cajero.gye@…`, `entrenador.uio@…`, `nutricionista.gye@…`, `contador@…`, `alumno.uio@…`). Ya está creado y cargado en este equipo. Si necesita crearlo de nuevo en otro equipo:
   ```powershell
   $e = @{}; Get-Content infra\compose\.env | % { $p = $_ -split '=',2; if ($p.Count -eq 2) { $e[$p[0]] = $p[1] } }
   ./scripts/demo-seed.ps1 -PlatformEmail $e['PLATFORM_ADMIN_EMAIL'] -PlatformPassword $e['PLATFORM_ADMIN_PASSWORD'] -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test
   ./scripts/analytics-demo-data.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test -ConDatosPasados
   ```
   `analytics-demo-data.ps1` crea por la API 16 alumnos con membresías y pagos repartidos en los últimos 28 días (efectivo, tarjeta, transferencia sin verificar, abonos, dos pagos revertidos y un pago repartido en dos obligaciones), entradas al gimnasio, una clase con reservas, lista de espera y una sesión cancelada, y un cierre de caja con 5 dólares de faltante. Con `-ConDatosPasados` (necesita Docker) mueve fechas en la base para ver saldos vencidos y una clase ya realizada. **Cárguelo una sola vez**: repetirlo duplica los datos.
3. **Historial de membresías** (para ver retención, renovación y vencimientos): el lote anterior deja todas las membresías recién vendidas. Ejecute una vez:
   ```powershell
   ./scripts/analytics-demo-history.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test
   ```
   Toma 5 membresías cobradas y mueve sus fechas en la base (necesita Docker): 3 vencieron hace 5 días (la primera se renueva por la API y la segunda queda sin renovar), una vence en 5 días y otra en 20 días. **Cárguelo una sola vez.**
4. (El gimnasio `revision` también tiene datos, pero de varias cargas de prueba: sus cifras son mayores y no sirven para comparar con esta guía.)

## 1. Ver todo de una vez desde la consola

```powershell
./scripts/analytics-check.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test                      # propietario, últimos 30 días
./scripts/analytics-check.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test -Periodo Last7Days   # con comparación (hay pagos en la semana anterior)
./scripts/analytics-check.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test -Usuario cajero.gye # lo que ve cada rol
```
Períodos: `Today`, `Last7Days`, `Last30Days`, `CurrentMonth`, `PreviousMonth`, `CurrentYear`. El script solo lee; imprime contexto, dashboard, evolución y los cinco reportes financieros.

## 2. Valores esperados (propietario, `CurrentYear` o `Last30Days`, cargados el 28/10/2026)

> Estas cifras son las del lote **antes** de ejecutar el historial de membresías (paso 0.3). Después del historial cambian solo: saldo pendiente 388,33 en 11 obligaciones (la renovación suma una cuota de 30,00 sin pagar), alumnos activos 9 y nuevas inscripciones 11 en `Last30Days` (5 membresías quedan fuera de los últimos 30 días). El resto no cambia.

| Qué mirar | Debe dar |
|---|---|
| Total cobrado | **391,67 USD** en **11** pagos (promedio 35,61) |
| Cobrado en efectivo / otros medios | 110,00 / 281,67 (Card 71,9 % · Cash 28,1 %) |
| Transferencias por verificar | 110,00 (**no** suman al cobrado) |
| Pagos revertidos | 60,00 (2 pagos; **no** suman al cobrado) |
| Pagos parciales (abonos) | 91,67 USD en 4 pagos |
| Obligaciones generadas | 640,00 |
| Saldo pendiente / vencido (hoy) | 358,33 en 10 obligaciones / 153,33 en 4 |
| Antigüedad | Al día 205,00 · 1-30 días 70,00 · 31-60 días 83,33 |
| Cobrado por plan | Trimestral 226,67 · Mensual 145,00 · Cobros sin plan 20,00 (la camiseta) → suman 391,67 |
| Alumnos nuevos / activos / nuevas inscripciones | 16 / 12 / 16 |
| Asistencia al gimnasio | 6 |
| Clases | 1 realizada · 0 canceladas en el período (la sesión cancelada cae en el futuro cercano) |
| Ocupación de clases | 66,7 % (solo cuentan reservas confirmadas de sesiones no canceladas) |
| Caja | 1 turno cerrado: esperado 160,00 · contado 155,00 · diferencia −5,00 (faltante 5,00) |

Los importes se calculan con las fechas de carga: los pagos tienen entre 1 y 28 días de antigüedad, por lo que `Last30Days` y `CurrentYear` coinciden hoy; en los próximos días `Last30Days` irá perdiendo los más antiguos y `CurrentYear` no.

## 3. Pruebas por tema

### 3.1 Cobrado, por verificar y revertido (reglas financieras)
1. Compare **Total cobrado (391,67)** con la suma de «Cobrado en efectivo + otros medios» (110 + 281,67) → debe coincidir.
2. Las transferencias por verificar (110,00) y los revertidos (60,00) **no** están en el cobrado.
3. En el reporte de cobros, **por sucursal** y **por plan** suman exactamente 391,67 (nada se cuenta dos veces: la camiseta pagada junto con una cuota aparece en «Cobros sin plan» por 20,00 y el resto en su plan).
4. Pida el detalle de revertidos: `…/reports/financial/collections?preset=CurrentYear&status=Reversed` lista los 2 pagos revertidos y su resumen sigue en 391,67. `status=All` trae los 15 pagos del período (11 confirmados + 2 por verificar + 2 revertidos); `status=PendingVerification` trae los 2 por verificar.
5. `…/reports/financial/reversals?preset=CurrentYear`: 2 pagos revertidos (60,00) con su motivo («Cobro duplicado en el datáfono»), quién los revirtió y ambas fechas.

### 3.2 Comparación con el período anterior
1. Ejecute con `-Periodo Last7Days`: el cobrado de esta semana debe mostrar **variación con porcentaje** (hay cobros en la semana anterior: 70,00).
2. Con `-Periodo Last30Days` el comparable (30 días anteriores) no tiene cobros: debe decir **«Sin base de comparación»**, nunca un porcentaje infinito.
3. «Saldo pendiente», «Saldo vencido» y «Próximas a vencer» dicen **«medida de hoy»**: no se comparan.
4. Los indicadores sin información aparecen como **(vacío)**; la ocupación sin sesiones es **«Sin datos»**, no 0 %.

### 3.3 Saldos pendientes
1. Dashboard: pendiente 358,33; vencido 153,33.
2. `…/reports/financial/outstanding-balances` (lista por alumno, el que más debe vencido primero) y `?view=Obligations` (por obligación con días de atraso). `?overdueOnly=true` deja solo lo vencido (153,33).
3. Debe coincidir exactamente con la cartera que ya existía: `GET /api/v1/receivables/summary` (pendiente 358,33 y vencido 153,33).
4. Ningún listado trae teléfono, correo ni documento del alumno: solo código y nombre.

### 3.4 Alumnos y clases
1. «Alumnos activos» = alumnos con una membresía **vigente y ya cobrada**: son 12 (los 4 con transferencia sin verificar y sin pagar no cuentan).
2. En «Operación»: 6 entradas, ocupación 66,7 %, 1 inasistencia (de la clase ya realizada), y las sesiones del período separadas en realizadas / canceladas / por ocurrir / pendientes de cierre.

### 3.5 Permisos por rol (servidor)
Ejecute el script con cada usuario (`-Usuario`) y compruebe:

| Usuario | Sucursales | Secciones que recibe | Total cobrado |
|---|---|---|---|
| `propietario` | Guayaquil + Quito | finanzas, alumnos, operación, instructor | 391,67 |
| `admin.uio` | solo Quito | finanzas, alumnos, operación | 391,67 (toda la plata está en Quito) |
| `contador` | ambas | finanzas, alumnos (**sin** operación) | 391,67 |
| `cajero.gye` | solo Guayaquil | solo finanzas | **0,00** (en Guayaquil solo hay una transferencia por verificar de 110,00) |
| `recepcion.uio` | solo Quito | solo operación | no tiene reportes financieros |
| `entrenador.uio` | solo Quito | solo instructor (sus sesiones) | no tiene reportes financieros |
| `nutricionista.gye` | — | ninguna | **HTTP 403** |

Además, en Swagger: con `admin.uio` pida un reporte con `branchId` de Guayaquil → **403** («No tiene acceso a esta sucursal»). Con `recepcion.uio` pida `/api/v1/reports/financial/collections` → **403**.

### 3.6 Fechas y zona horaria
1. `GET /api/v1/analytics/context?preset=Custom&from=2026-10-01&to=2026-10-10` devuelve `startUtc = 2026-10-01T05:00:00Z` y `endUtcExclusive = 2026-10-11T05:00:00Z` (Ecuador es UTC−5: el día local empieza a las 05:00 UTC).
2. Rango invertido (`from` > `to`), de más de 366 días, sin fechas con `Custom`, o fechas junto a un preajuste → **422** con su código (`analytics.period_invalid`, `…_too_long`, `…_required`, `…_preset_with_dates`).
3. `expiringDays` en el dashboard solo acepta 7, 15 o 30; otro valor → 422 `analytics.expiring_days_invalid`.

### 3.7 Alumnos y membresías (bloque 5; requiere haber ejecutado el historial del paso 0.3)
Todos exigen `reports.members.read` (propietario, administrador de sucursal y contador). Con `Last30Days` y los datos de `analitica` se espera:

| Reporte | Qué comprobar | Debe dar |
|---|---|---|
| `/reports/members/summary` | Estado de las membresías (hoy) | Activas 9 · Vencidas 3 · Pendientes de pago 5 · Congeladas 0 · Canceladas 0 |
| | Alumnos activos / nuevos | 9 / 16 (activo = ficha activa con membresía vigente y **cobrada**) |
| | Por plan | Mensual 5 · Trimestral 4 |
| `/reports/members/retention` | Cohorte (activos el 28/09) / retenidos / bajas | 3 / 0 / 3 → **0 %** de retención (la primera renovó pero **aún no pagó** la renovación, por eso no cuenta como activa) |
| | Quiénes salieron | Lista con **código y nombre** (sin teléfono ni correo) |
| `/reports/members/renewals` | Vencidas / renovadas / sin renovar | 3 / 1 / 2 → **33,3 %** |
| `/reports/members/expirations?days=30` | Total y tramos | 6 en total: 0-7 días: 1 · 16-30 días: 5; cada fila indica si ya renovó y cuánto debe el alumno |
| `/reports/members/acquisitions` | Nuevas inscripciones / renovaciones / bajas | 11 / 1 / 3 (las 5 membresías movidas al pasado quedan fuera de los últimos 30 días) |
| | Tendencia | Un tramo por mes calendario (el primero y el último pueden ser parciales); la suma de los tramos es el total |

Compruebe además:
1. **Retención con filtro**: `retention?planId=<id de Mensual>` reduce la cohorte a quienes tenían ese plan.
2. **Una membresía cancelada no es una baja si el alumno tiene otra vigente**: la baja se mide por «sin membresía vigente al cierre».
3. **Sin base**: `retention?preset=Custom&from=2020-01-01&to=2020-01-31` → cohorte 0, porcentaje vacío («Sin base de comparación»), sin error.
4. **Próximos vencimientos**: `days` solo acepta 7, 15, 30, 60 o 90 (otro valor → 422 `analytics.expiration_days_invalid`); `hideRenewed=true` oculta a quienes ya renovaron.
5. **Historial de membresías**: `acquisitions?kind=Renewal|New|Reincorporation` filtra el listado paginado por tipo.
6. **Permisos**: `cajero.gye`, `recepcion.uio` y `entrenador.uio` reciben **403** en `/reports/members/*`; `contador` y `admin.uio` sí.

### 3.8 Operación y clases (bloque 6)
La asistencia al gimnasio exige `reports.operations.read` (propietario, administrador de sucursal y recepción). Los reportes de clases los ven quienes tienen operación (todas las clases de sus sucursales) **o** el permiso de instructor: el entrenador ve **solo sus propias sesiones** (el servidor lo filtra). Para incluir las clases de los próximos días (el lote crea sesiones de mañana en adelante) use un rango:

```powershell
./scripts/analytics-check.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test -Desde 2026-09-28 -Hasta 2026-11-04
./scripts/analytics-check.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test -Usuario entrenador.uio -Desde 2026-09-28 -Hasta 2026-11-04
```

| Reporte | Qué comprobar | Debe dar (rango 28/09–04/11, datos de `analitica`) |
|---|---|---|
| `/reports/attendance` | Visitas / alumnos distintos / frecuencia | 6 / 6 / 1,0 (un alumno por visita) |
| | Por sucursal | Quito Norte 6 · Guayaquil Centro **0** (la sucursal sin visitas aparece en cero, no se omite) |
| | Frecuencia | 6 alumnos vinieron 1 vez |
| | Días y horas | `byWeekday` de lunes a domingo y `byHour` de 0 a 23 (hora local de Quito, no UTC) |
| | `view=Members` | Alumnos con sus visitas: solo código y nombre |
| `/reports/classes/occupancy` | Sesiones | 1 realizada · 1 cancelada · 2 por ocurrir · 0 pendientes de cierre |
| | Ocupación reservada | 66,7 % = (2 + 1 + 3) / (3 + 3 + 3); la sesión **cancelada** no cuenta |
| | Ocupación asistida | 33,3 % = 1 asistente de los 3 cupos de la sesión realizada |
| | Demanda en espera | 2 personas esperando cupo: se informan como **demanda**, nunca como ocupación |
| | Tasa de asistencia | 50 % (1 asistió, 1 no asistió) |
| | Horarios más ocupados | El jueves 08:00 al 100 % (sesión llena) arriba de la lista |
| `/reports/classes/cancellations` | Sesiones / reservas canceladas | 1 / 1 (la reserva cancelada al cancelar la sesión **no** se cuenta como cancelación del alumno) |
| | Tasa de cancelación | 14,3 % = 1 / (6 confirmadas + 1 cancelada) |
| | Lista de sesiones canceladas | Con el motivo («Mantenimiento de la sala») y las reservas afectadas |

Compruebe además:
1. **Una sesión ya pasada que nadie cerró** aparece como *pendiente de cierre* y no como realizada.
2. **Sin sesiones** (rango sin clases): la ocupación es «Sin datos», nunca 0 %.
3. **Filtros**: `classes/occupancy?activity=Yoga` y `?classId=<id>` acotan las sesiones y los porcentajes.
4. **Agrupación**: `attendance?grouping=Week` agrupa por semana (el tramo empieza el lunes); `Month` por mes; `Auto` elige según el largo del rango.
5. **Permisos**: `entrenador.uio` recibe los reportes de clases (marcados «solo sus sesiones») pero **403** en `/reports/attendance` y en lo financiero; `recepcion.uio` ve asistencia y clases de Quito; `cajero.gye` y `contador` reciben **403** en los reportes de clases.

### 3.9 Comparación de sucursales (bloque 7)
`GET /reports/branches/comparison` exige el permiso de comparar (propietario, administrador de sucursal y contador). Compara las sucursales elegidas (`branchId` repetible; sin indicar, todas las que el usuario puede consultar) con **el mismo período, la misma zona horaria y las mismas definiciones** para todas. El script la imprime al final como «Comparación de sucursales».

Con `Last30Days` y los datos de `analitica` (después del historial) debe verse:

| Sucursal | Cobrado | Pendiente (hoy) | Activos | Inscripciones | Renovaciones | Visitas | Ocupación |
|---|---|---|---|---|---|---|---|
| Quito Norte | 391,67 (100 %) | 218,33 | 9 | 7 | 1 | 6 | 66,7 % |
| Guayaquil Centro | 0,00 (0 %) | 170,00 | 0 | 4 | 0 | 0 | **Sin datos** |
| **Total** | 391,67 | 388,33 | 9 | 11 | 1 | 6 | 66,7 % |

Compruebe:
1. **El total cuadra**: cobrado 391,67 = el del dashboard y el de «Cobros»; pendiente 388,33 = el de la cartera; inscripciones 7 + 4 = 11.
2. **Guayaquil no desaparece**: aparece en cero (sin visitas, ocupación «Sin datos» porque no tiene clases) y, si no tuviera ninguna actividad, marcada `hasActivity: false` («Sin datos»).
3. **La ocupación del total se calcula sobre las sumas** (Σ ocupados ÷ Σ capacidad), no promediando los porcentajes de cada sucursal.
4. **Evolución mensual**: `months` parte el período por mes calendario y `evolution` trae, por sucursal y alineados con esos meses, el cobrado, las inscripciones nuevas y las visitas. La suma de los meses es igual al total del período.
5. **Subconjunto**: `?branchId=<Quito>&branchId=<Guayaquil>` o solo una; el total solo suma las elegidas. Una sucursal fuera de su alcance o de otro gimnasio → **403**.
6. **Permisos por área**: `contador` ve cobros y alumnos pero **no** operación (las secciones de visitas y ocupación no viajan y `omittedSections` dice `operations`); `admin.uio` solo se compara con Quito; `cajero.gye`, `recepcion.uio` y `entrenador.uio` reciben **403**.

### 3.10 Exportaciones a CSV (bloque 8)
Cualquier reporte se puede exportar con los mismos filtros. Exige el permiso de **exportar** (propietario, administrador de sucursal y contador) **y** el del reporte. Desde la consola:

```powershell
./scripts/analytics-export.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test -Reporte Collections
./scripts/analytics-export.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test -Reporte OutstandingBalances -Usuario contador
```
Reportes: `Collections`, `PaymentMethods`, `OutstandingBalances`, `CashRegisters`, `Reversals`, `MemberAcquisitions`, `MemberRetention`, `MemberRenewals`, `MemberExpirations`, `Attendance`, `ClassOccupancy`, `ClassCancellations`, `BranchComparison`. El script guarda el archivo en `%TEMP%\fitcore-exportaciones` y muestra las primeras filas.

| Qué comprobar | Debe dar (propietario, `Last30Days`) |
|---|---|
| `Collections` | **11 filas** (los 11 pagos confirmados), 1.392 bytes aprox.; la primera fila es el encabezado con acentos correctos |
| `OutstandingBalances` | 11 filas (una por alumno con saldo), ordenadas por lo vencido |
| `BranchComparison` | 3 filas: Quito Norte, Guayaquil Centro y el total; igual a la comparación en pantalla |
| Nombre del archivo | `reporte-cobros-AAAAMMDD-xxxxxxxx.csv`: nunca lleva nombres de alumnos |
| `cajero.gye` | **403**: puede ver reportes financieros pero no tiene el permiso de exportar |
| `contador` pidiendo `ClassOccupancy` | **403**: puede exportar, pero no tiene permiso sobre las clases |

Más comprobaciones (con Swagger o el script):
1. **Mismos filtros y alcance**: `POST /api/v1/reports/exports` acepta los mismos filtros del reporte (`preset`, `from`, `to`, `branchIds`, `planId`, `status`, `method`, `view`, `days`…). `admin.uio` solo exporta lo de Quito, y pedir Guayaquil → 403.
2. **Archivo seguro para Excel**: UTF-8 con marca de orden de bytes, fin de línea CRLF, números con punto decimal y fechas en **hora local** (no UTC). Todo el texto va entre comillas y, si empieza por `=`, `+`, `-` o `@`, lleva un apóstrofo delante para que no se ejecute como fórmula (los números negativos, como una diferencia de −5,00, no se tocan).
3. **Descarga protegida**: `GET /api/v1/reports/exports/{id}/download` exige sesión, el permiso de exportar y el del reporte, y **solo la puede hacer quien la pidió** (otro usuario, aunque sea del mismo gimnasio, recibe 404). Responde con `Cache-Control: private, no-store`. No existen enlaces públicos.
4. **Vencimiento**: cada archivo dura 24 horas (`expiresAt`); después la descarga responde 404 `analytics.export_expired`, el archivo se borra y el registro queda como `Purged`. `GET /api/v1/reports/exports` lista las propias con su estado y el número de descargas.
5. **Límites**: tope de **20.000 filas** por exportación (si se supera, se rechaza con 422 `analytics.export_too_large` y un mensaje para acotar los filtros, nunca se entrega un archivo recortado) y **10 exportaciones por hora y por usuario** (la undécima → 409 `analytics.export_rate_limited`).
6. **Registro**: la tabla `analytics.report_exports` guarda quién, cuándo, qué reporte, filtros y filas (sin contenido); el servidor no puede borrar esos registros.

## 4. Usar Swagger en lugar del script
1. Abra http://localhost:8080/swagger.
2. `POST /api/v1/auth/login` con `{"tenantSlug":"analitica","email":"propietario@analitica.fitcore.test","password":"DemoFit2026x"}` y copie `accessToken`.
3. Botón **Authorize** y pegue el token.
4. Pruebe, en el grupo **Analytics** y **Reports: financial**:
   - `GET /api/v1/analytics/context`
   - `GET /api/v1/analytics/dashboard` y `GET /api/v1/analytics/dashboard/comparison`
   - `GET /api/v1/reports/financial/collections` · `…/payment-methods` · `…/outstanding-balances` · `…/cash-registers` · `…/reversals`
   - `GET /api/v1/reports/members/summary` · `…/acquisitions` · `…/retention` · `…/renewals` · `…/expirations`
   - `GET /api/v1/reports/attendance` · `…/reports/classes/occupancy` · `…/reports/classes/cancellations` · `…/reports/branches/comparison`
   - `POST /api/v1/reports/exports` · `GET /api/v1/reports/exports` · `GET /api/v1/reports/exports/{id}` · `GET /api/v1/reports/exports/{id}/download`
5. Parámetros comunes: `preset` (Today, Last7Days, Last30Days, CurrentMonth, PreviousMonth, CurrentYear, Custom), `from`/`to` (solo con Custom), `branchId` (repetible). Cobros: `status` (Confirmed, PendingVerification, Reversed, All), `method` (Cash, BankTransfer, Card, Other), `planId`, `page`, `pageSize`.

## 3.11 Pantallas (bloque 9)

Con el gimnasio de práctica `analitica` en http://localhost:4200 (o `ng serve` en :4300):

1. **Panel de analítica** (menú «Panel de analítica»): indicadores por área según el rol. Cada tarjeta trae el período, la variación frente al comparable (con "Sin base de comparación" si el anterior es 0) y, en «Qué mide este indicador», su definición. Cambie el período (Hoy, 7/30 días, mes actual/anterior, año, personalizado) y la sucursal; un rango invertido o de más de 366 días se rechaza en pantalla.
2. **Reportes financieros / de alumnos / operativos**: cada pestaña usa los mismos filtros y debe cuadrar con el panel y con `scripts/analytics-check.ps1`. Los saldos y estados son una "foto de hoy" y lo dicen.
3. **Comparar sucursales**: fila Total cuadra con la suma de las sucursales.
4. **Exportar CSV** (botón en cada reporte) y **Mis exportaciones**: descarga el archivo y lo vuelve a descargar desde el historial; los archivos vencen a las 24 horas.
5. **Roles**: un Entrenador ve solo «Mis clases (analítica)»; un rol sin permisos de reportes no ve el menú y la URL directa lo redirige.

## 5. Qué NO está todavía
- Exportación a PDF (diferida: no hay biblioteca instalada) y medición de rendimiento con volumen (bloque 10).
- Medición de rendimiento con volumen y revisión de planes de ejecución: bloque 10.
