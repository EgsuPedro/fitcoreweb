# Guía de validación manual — Fase 6 (analítica): bloques 2 a 5

Hasta el bloque 5 la fase entrega **API** (contexto, dashboard, reportes financieros y reportes de alumnos y membresías); las pantallas de Angular llegan en el bloque 9. Esta guía permite validar todo lo hecho **sin pantallas**, con datos reales y con los usuarios de cada rol. Se irá ampliando con cada bloque.

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

## 4. Usar Swagger en lugar del script
1. Abra http://localhost:8080/swagger.
2. `POST /api/v1/auth/login` con `{"tenantSlug":"analitica","email":"propietario@analitica.fitcore.test","password":"DemoFit2026x"}` y copie `accessToken`.
3. Botón **Authorize** y pegue el token.
4. Pruebe, en el grupo **Analytics** y **Reports: financial**:
   - `GET /api/v1/analytics/context`
   - `GET /api/v1/analytics/dashboard` y `GET /api/v1/analytics/dashboard/comparison`
   - `GET /api/v1/reports/financial/collections` · `…/payment-methods` · `…/outstanding-balances` · `…/cash-registers` · `…/reversals`
   - `GET /api/v1/reports/members/summary` · `…/acquisitions` · `…/retention` · `…/renewals` · `…/expirations`
5. Parámetros comunes: `preset` (Today, Last7Days, Last30Days, CurrentMonth, PreviousMonth, CurrentYear, Custom), `from`/`to` (solo con Custom), `branchId` (repetible). Cobros: `status` (Confirmed, PendingVerification, Reversed, All), `method` (Cash, BankTransfer, Card, Other), `planId`, `page`, `pageSize`.

## 5. Qué NO está todavía
- **Pantallas**: llegan en el bloque 9 (hoy se valida por API/consola).
- Reportes operativos (asistencia por día, ocupación por sesión, demanda), comparación de sucursales y exportaciones CSV: bloques 6 a 8.
- Medición de rendimiento con volumen y revisión de planes de ejecución: bloque 10.
