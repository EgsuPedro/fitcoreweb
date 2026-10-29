# Informe de la Fase 6 — Dashboards, reportes e inteligencia de negocio

Corresponde a la «FASE 5» del prompt del usuario (nuestra numeración: 6). Módulo `FitCore.Analytics`, solo de lectura. Decisiones, reglas de fechas y fórmulas: [ADR 0011](adr/0011-modulo-analytics.md). Definición de cada indicador: [analytics-indicadores.md](analytics-indicadores.md). Validación manual con datos de práctica: [guia-pruebas-fase-6.md](guia-pruebas-fase-6.md). Fuera de la fase: facturación electrónica SRI, nómina y contabilidad.

## Qué se entrega
| Bloque | Contenido | Estado |
|---|---|---|
| 1 | Diagnóstico del repositorio y ADR 0011 | Hecho |
| 2 | Módulo, permisos, períodos y variación (sin base de comparación) | Hecho |
| 3 | Dashboard ejecutivo y por rol, con series frente al período comparable | Hecho |
| 4 | Reportes financieros: cobros, medios de pago, saldos, caja, reversiones | Hecho |
| 5 | Alumnos y membresías: resumen, altas y bajas, retención, renovaciones, vencimientos | Hecho |
| 6 | Operación: asistencia, ocupación y cancelaciones de clases, vista del instructor | Hecho |
| 7 | Comparación de sucursales | Hecho |
| 8 | Exportación a CSV con vencimiento, privada y auditada | Hecho |
| 9 | Pantallas Angular | Hecho |
| 10 | Rendimiento medido, regresión, documentación e informe | Hecho |

## Decisiones principales (confirmadas el 2026-10-28)
- Módulo nuevo y de **solo lectura**; lee los demás módulos por vistas `*Ref` (lo vigila una prueba de arquitectura). Su única tabla propia es `analytics.report_exports`.
- Exportaciones: **CSV síncrono** guardado 24 h en el almacén privado, tope de 20.000 filas; **PDF diferido** (no hay biblioteca).
- «Alumno activo» = ficha activa **con una membresía vigente y cobrada el último día del período**.
- **Sin cachés, tablas de resumen ni vistas materializadas**; toda agregación va en PostgreSQL.
- Lo **cobrado no es utilidad**: no hay costos ni gastos.

## Cómo se garantiza la corrección
- **Variación honesta:** si el período anterior vale 0, el valor es «Sin base de comparación» (nunca un porcentaje); las razones varían en puntos porcentuales; las «fotos de hoy» no se comparan.
- **Un solo origen de cada cifra:** el cobrado es la suma de `payments.amount` (nunca se une con las asignaciones, que contarían dos veces); el saldo reproduce el de Cartera. Los reportes cuadran entre sí (panel, detalle, comparación y CSV) y con `scripts/analytics-check.ps1`.
- **Alcance en el servidor:** cada sección exige su permiso; las sucursales se validan (403 `branches.not_allowed`); el instructor solo ve sus sesiones; un área sin permiso no viaja en la respuesta. La interfaz solo oculta lo que el servidor ya niega.
- **Fechas:** los filtros son fechas locales del gimnasio (o de la sucursal si hay una sola), con intervalos medio abiertos en UTC; máximo 366 días.
- **Aislamiento:** RLS forzada, filtros globales por gimnasio y tabla de exportaciones sin `DELETE` para el rol de la aplicación.
- **CSV seguro:** UTF-8 con BOM, todo texto entrecomillado, celdas que empiezan por `=`, `+`, `-` o `@` neutralizadas, descarga solo para quien la pidió (otro usuario recibe 404), permisos revalidados al descargar, 10 exportaciones por hora y usuario, y archivos vencidos purgados.

## Migraciones
- Analytics: `InitialAnalytics` → `AddAnalyticsSecurity` (tabla `analytics.report_exports`, RLS forzada, claves compuestas, restricciones, disparador de guarda).
- Commercial: `AddMembershipRenewalIndex` — índice parcial `(tenant_id, renewed_from_id) WHERE renewed_from_id IS NOT NULL`. Justificado por la medición (ver más abajo).

## Endpoints (bajo `/api/v1`)
`GET /analytics/context`, `/analytics/dashboard`, `/analytics/dashboard/comparison`; financieros `GET /reports/financial/collections|payment-methods|outstanding-balances|cash-registers|reversals`; alumnos `GET /reports/members/summary|acquisitions|retention|renewals|expirations`; operación `GET /reports/attendance`, `/reports/classes/occupancy|cancellations`; `GET /reports/branches/comparison`; exportaciones `POST|GET /reports/exports`, `GET /reports/exports/{id}`, `GET …/{id}/download`. Filtros comunes: `preset` (Today, Last7Days, Last30Days, CurrentMonth, PreviousMonth, CurrentYear, Custom), `from`, `to`, `branchIds`. Los listados se paginan (máximo 100 por página).

## Permisos nuevos
`analytics.dashboard.read`, `reports.financial.read`, `reports.members.read`, `reports.operations.read`, `reports.instructor.read`, `reports.compare`, `reports.export`. Plantillas de rol y [matriz](permissions-matrix.md) actualizadas. Los gimnasios creados antes de la fase necesitan volver a ejecutar `scripts/demo-seed.ps1` para recibirlos.

## Pantallas (Angular, español por defecto, inglés disponible)
Menú «Panel de analítica», «Reportes financieros», «Reportes de alumnos», «Reportes operativos» (o «Mis clases (analítica)» para el instructor), «Comparar sucursales» y «Mis exportaciones», cada uno con su permiso. Filtros compartidos (período, rango personalizado validado, sucursales), tarjetas con definición, gráficos SVG propios (sin biblioteca), estados de carga, error y vacío, tablas con encabezados accesibles y diseño adaptable.

## Rendimiento medido (bloque 10)
**Método.** `scripts/analytics-volume.mjs` carga un gimnasio de prueba (`volumen`) con 4 sucursales, **20.000 alumnos, 40.000 membresías, ≈38.000 obligaciones, ≈36.000 pagos, ≈368.000 entradas al gimnasio, 11.680 sesiones de clase y 175.200 reservas** (12 meses de historia, con PostgreSQL en Docker de desarrollo, sin ajustes). `scripts/analytics-perf.mjs` llama a cada endpoint 5 veces por la API real, con el propietario, y reporta la mediana. Se revisaron las consultas lentas con `log_min_duration_statement` y `EXPLAIN (ANALYZE)`.

**Hallazgo y corrección.** «Renovaciones» tardaba **≈2,2 s** aunque el período fuese de 30 días: cuatro consultas de ≈0,5 s que, por cada membresía vencida, buscaban su renovación recorriendo toda la tabla (`renewed_from_id` no tenía índice). Con el índice parcial: **≈80 ms** (30 días) y ≈360 ms (366 días), 28 veces menos. Es el único índice nuevo; el resto de los planes usa los existentes.

**Resultados tras el índice** (mediana en ms; 30 días / año de 366 días):

| Endpoint | 30 días | 366 días |
|---|---|---|
| `analytics/dashboard` | 528 | 967 |
| `analytics/dashboard/comparison` | 103 | 455 |
| `financial/collections` | 236 | 522 |
| `financial/payment-methods` | 50 | 59 |
| `financial/outstanding-balances` (foto de hoy) | 356 | 262 |
| `financial/cash-registers` | 64 | 33 |
| `financial/reversals` | 74 | 49 |
| `members/summary` | 499 | 343 |
| `members/acquisitions` | 709 | 2113 |
| `members/retention` | 563 | 409 |
| `members/renewals` | 79 | 359 |
| `members/expirations` | 79 | 74 |
| `attendance` (resumen / por alumno) | 33 / 46 | 1794 / 2188 |
| `classes/occupancy` | 288 | 1668 |
| `classes/cancellations` | 100 | 323 |
| `branches/comparison` | 296 | 1116 |

**Lectura honesta.**
- Con este volumen todo responde en **menos de 1 s en 30 días** y en **menos de 2,3 s en un año completo**. No se añadió ningún caché.
- Los años más lentos son agregaciones reales sobre muchas filas: asistencia (368.000 entradas, `COUNT(DISTINCT)` con ordenación que se desborda a disco con `work_mem` de 4 MB) y ocupación (175.200 reservas).
- «Altas y bajas» a un año (2,1 s) hace la tendencia mensual con ≈5 consultas de 60–80 ms por mes (12 meses): son muchas consultas pequeñas, no una grande. Se deja anotado como la primera candidata si el volumen crece (calcular la retención de todos los meses en una sola consulta).
- Las cifras son de un contenedor de desarrollo, no de producción; sirven para comparar antes y después y para detectar consultas sin índice, no como garantía.
- No se añadieron índices a pagos, asistencia ni sesiones: las consultas de rango sobre ellos usan los existentes o leen casi toda la tabla del gimnasio, donde un índice no ayuda.

## Pruebas (regresión final)
Regresión del 2026-10-28 (a las 19:00–20:00 hora de Ecuador), en local, contra PostgreSQL real:

| Suite | Resultado |
|---|---|
| Unitarias SharedKernel / Analytics / Classes / Commercial / Wellness | 13 / 42 / 57 / 144 / 131 correctas |
| Arquitectura (límites entre módulos, filtros de tenant, endpoints con autorización, paridad de políticas) | 25 correctas |
| Integración (API + PostgreSQL con Testcontainers) | 536 correctas (52 de analítica) |
| Angular: Vitest | 83 correctas |
| Angular: claves de traducción es/en | 1134 claves, todas presentes |
| Angular: compilación de producción | correcta |
| e2e Playwright (navegador real, API, base y Worker reales) | 41 correctas (36 previas + 5 de analítica) |

Cubre, entre otras cosas: aislamiento entre gimnasios, alcance por sucursal y por rol, permisos por endpoint, períodos y zona horaria (medianoche local frente a UTC), variación sin base, cuadre entre dashboard / reportes / comparación / Cartera, pagos parciales y revertidos, retención y renovación, ocupación sin contar la espera, CSV (inyección de fórmulas, caracteres especiales, tope, vencimiento, descarga ajena, límite por hora) y paridad de políticas de autorización.

## Corrección de un defecto encontrado por las pruebas
Al alta de un alumno, `enrolled_on` se guardaba con la **fecha UTC**; entre las 19:00 y las 24:00 en Ecuador quedaba con la fecha de mañana y el alumno no entraba en «Alumnos nuevos» del día. Ahora usa la fecha local de la sucursal, como ya hacían las membresías.

## Pendiente y límites conocidos
- **PDF**: diferido (no hay biblioteca de generación).
- **Purga de exportaciones vencidas**: hoy es perezosa (al consultar); no hay tarea periódica.
- **Errores de descarga**: una descarga fallida muestra un mensaje genérico; el código de error llega dentro de un archivo y no se interpreta.
- **Saldos**: son una foto de hoy; no se puede consultar el saldo de una fecha pasada porque el sistema no lo guarda.
- **Tendencia mensual de «Altas y bajas»**: ver la nota de rendimiento.
- **CI de GitHub**: `gh` no está instalado; las ejecuciones de las fases 5 y 6 no se han verificado desde aquí.
- Heredado: envío real de WhatsApp, recuperación de contraseña, facturación SRI, inventario y app móvil.
