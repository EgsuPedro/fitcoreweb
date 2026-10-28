# Modelo de base de datos

PostgreSQL 17. Un solo `DbContext` (`PlatformDbContext`) para el módulo Platform. Los módulos de negocio futuros tendrán su propio contexto y esquema. Nombres en `snake_case`.

## Esquemas y tablas

| Esquema | Tabla | Contenido | `tenant_id` | RLS |
|---|---|---|---|---|
| organizations | `tenants` | Gimnasio: nombre, slug único, estado, zona horaria, idioma, contacto | (es la raíz) | No |
| organizations | `branches` | Sucursal: código único por tenant, código de establecimiento SRI, ubicación, estado | Sí | Sí |
| identity | `app_users` | Usuarios (Identity). Correo único **por tenant**, bloqueo, estado activo | Sí | Sí |
| identity | `app_roles` | Roles por tenant, indicador "todas las sucursales" | Sí | Sí |
| identity | `user_roles` | Usuario ↔ rol (FK compuestas con tenant) | Sí | Sí |
| identity | `permissions` | Catálogo global de permisos (se siembra al arrancar) | No | No |
| identity | `role_permissions` | Rol ↔ permiso | Sí | Sí |
| identity | `user_branches` | Sucursales a las que accede un usuario | Sí | Sí |
| identity | `refresh_tokens` | Solo el hash SHA-256 del secreto, familia, rotación, revocación | Sí | Sí |
| audit | `audit_logs` | Auditoría append-only | Sí | Sí |
| messaging | `outbox_messages` | Eventos de integración pendientes de publicar | No | No |

## Integridad
- Claves primarias `uuid`. Las tablas de un tenant tienen clave alterna `(tenant_id, id)` y las relaciones entre ellas usan **claves foráneas compuestas**, de modo que la base de datos rechaza relacionar filas de gimnasios distintos.
- Restricciones únicas: slug de tenant; `(tenant_id, código)` de sucursal; `(tenant_id, correo normalizado)` de usuario; `(tenant_id, nombre)` de rol.
- Estado de tenant restringido con `CHECK`. Longitudes y obligatoriedad explícitas en la configuración de EF.
- Concurrencia optimista con la columna de sistema `xmin` en `tenants`, `branches` y `refresh_tokens` (usuarios y roles usan el `ConcurrencyStamp` de Identity).
- Fechas en UTC. Las fechas de creación/modificación y sus autores se rellenan en el interceptor de auditoría.
- Índices por `(tenant_id, occurred_at)` y `(tenant_id, entity, entity_id)` en auditoría; índice parcial de la outbox sobre mensajes sin procesar.

## Aislamiento
1. El tenant sale solo del JWT.
2. Filtro global de EF + verificación en `SaveChanges`.
3. Row-Level Security con `FORCE` en las 8 tablas con `tenant_id` de datos de negocio; política `tenant_isolation` con `tenant_id = current_setting('app.tenant_id')`. La conexión fija `app.tenant_id` al abrirse.
4. Claves foráneas compuestas.

La aplicación se conecta con el rol `fitcore_app` (sin superusuario ni `BYPASSRLS`); solo tiene `SELECT` e `INSERT` en `audit.audit_logs`. Las migraciones se aplican con otro usuario (propietario del esquema).

## Migraciones
| Migración | Contenido |
|---|---|
| `InitialCore` | Esquemas, tablas, índices, claves y concurrencia |
| `AddRowLevelSecurity` | RLS forzado y políticas, FKs compuestas de `user_roles`, CHECK de estado, permisos del rol de aplicación |

### Aplicar
```powershell
# Desarrollo y pruebas: la API aplica las migraciones al arrancar (Database:AutoMigrate=true).
# Manual, con el usuario propietario:
$env:FITCORE_MIGRATIONS_CONNECTION = "Host=localhost;Port=5433;Database=fitcore;Username=<propietario>;Password=<clave>"
dotnet tool restore
dotnet ef database update --project src/backend/Modules/Platform/FitCore.Platform --startup-project src/backend/FitCore.Api
```

### Comprobar el esquema
```powershell
docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env exec postgres psql -U <propietario> -d fitcore -c "\dt *.*" 
# Migraciones aplicadas:
#   SELECT "MigrationId" FROM "__EFMigrationsHistory";
# RLS:
#   SELECT relname, relrowsecurity, relforcerowsecurity FROM pg_class WHERE relname IN ('app_users','branches','audit_logs');
```
La prueba `Las_migraciones_estan_aplicadas_y_el_esquema_tiene_las_tablas_y_RLS_esperadas` hace esta comprobación automáticamente contra una base real en cada ejecución.

### Crear una migración nueva
```powershell
dotnet ef migrations add <Nombre> --project src/backend/Modules/Platform/FitCore.Platform --startup-project src/backend/FitCore.Api --output-dir Persistence/Migrations
```
Nunca se usa `EnsureCreated`. No se ejecutan migraciones destructivas ni se eliminan volúmenes sin una orden explícita.

## Módulo comercial (Fase 3, `CommercialDbContext`)
Migraciones propias, con su historial en `members.__ef_migrations_history`. Mismo aislamiento que el núcleo: `tenant_id`, RLS forzado, FK compuestas `(tenant_id, id)` y FK a `organizations.branches (tenant_id, id)`.

| Esquema | Tabla | Contenido |
|---|---|---|
| members | `members` | Alumnos. Único `(tenant_id, tipo, número de documento)` y `(tenant_id, código)` |
| members | `counters` | Contadores por gimnasio (código de alumno `A-000001`), incremento atómico |
| members | `membership_plans`, `plan_branches` | Planes (precio con IVA incluido, duración, congelamiento) y sucursales donde se venden (vacío = todas) |
| members | `promotions`, `promotion_plans` | Promociones con vigencia, usos y planes aplicables (vacío = todos) |
| members | `commercial_settings` | Políticas del gimnasio (acceso con deuda, % mínimo, días de vencimiento, descuento máximo) |
| members | `memberships`, `membership_events` | Membresías con copia de los términos aplicados (precio, descuento, IVA, congelamiento) e historial append-only. Restricción de exclusión contra solapamientos por alumno |
| billing | `payment_obligations` | Lo que debe cada alumno: importe original, descuento, exigible, subtotal e IVA con la tarifa congelada. El saldo no se guarda (bloque 5) |
| billing | `payments`, `payment_allocations` | Pagos inmutables y sus asignaciones a obligaciones. Triggers: asignaciones ≤ exigible, pago inmutable, un reverso no se reactiva. Sin borrado para el rol de la aplicación |
| billing | `idempotency_keys` | Clave + huella de la solicitud + respuesta guardada, por gimnasio |
| cash | `cash_registers`, `cash_sessions`, `cash_movements` | Cajas por sucursal, turnos con apertura y arqueo, y movimientos solo-anexado. Índices únicos parciales: una sesión abierta por caja exclusiva y por usuario. Triggers: movimientos solo en sesión abierta e inmutables, sesión cerrada definitiva |
| training | `plans`, `plan_versions`, `plan_days`, `plan_exercises` | Rutinas versionadas: la cabecera apunta a la versión vigente (FK diferida); versiones, días y ejercicios prescritos son inmutables (trigger y sin UPDATE/DELETE). `plan_exercises` guarda copia del nombre y modo de registro, con CHECKs de prescripción por modo |
| training | `training_assignments` | Rutina fijada a una versión; una sola abierta por alumno (índice único parcial); solo se cierra (trigger), sin borrar |
| training | `workout_sessions`, `workout_session_exercises`, `workout_sets` | Sesiones con la versión usada y series reales; solo-agregar (el único cambio permitido es el comentario del entrenador, una vez); CHECKs de serie por modo |
| nutrition | `plans`, `plan_versions`, `meals`, `meal_items` | Planes nutricionales versionados: cabecera → versión vigente (FK diferida); versiones, comidas y alimentos son inmutables. Valores nutricionales opcionales con CHECKs de rango |
| nutrition | `nutrition_assignments` | Plan fijado a una versión; una sola abierta por alumno (índice único parcial); solo se cierra |
| nutrition | `dietary_profiles` | Alergias, intolerancias y restricciones (`text[]`); solo-agregar, el vigente es el más reciente; nunca vacío |
| nutrition | `meal_logs` | Cumplimiento por comida y día (único por alumno, comida y día); solo-agregar, con la versión del plan |
| wellness | `body_assessments` | Evaluaciones físicas solo-agregar (trigger): mediciones opcionales con CHECKs de rango, grasa con su método, fuente, corrección única por `corrects_id` |
| wellness | `photo_consents` | Consentimiento para guardar fotos de progreso: solo-agregar (trigger + sin UPDATE/DELETE para la app), versión del aviso y representante si es menor; el estado vigente es la última fila |
| wellness | `progress_photos` | Datos de cada foto (el archivo está en el almacén privado bajo una clave aleatoria, validada por CHECK); un trigger exige que referencie el consentimiento vigente, no se edita; retirar el consentimiento las borra |
| wellness | `goals` | Objetivos de un alumno: categoría/medida coherentes (CHECK), solo cambian al cerrarse (trigger), nunca se borran; el progreso no se guarda, se calcula al leer |
| training | `exercises` | Biblioteca: `tenant_id` nulo = catálogo base global. RLS por operación (todos leen globales y lo propio; solo plataforma escribe globales; nadie borra). Nombres únicos sin distinguir mayúsculas (global y por gimnasio), enlace solo https, modo de registro |
| training | `trainer_profiles` | Perfil de entrenador por usuario (único por gimnasio): especialidades `text[]`, certificaciones, biografía, estado. FK al usuario |
| training | `trainer_availability` | Franjas semanales del entrenador (`day` 0–6, `end_time > start_time`), sucursal opcional |
| training | `trainer_assignments` | Alumno–entrenador con historial. Índice único parcial: un solo activo por alumno. Trigger: solo cambia al cerrarse; cerrada es inmutable. Sin borrado |
| training | `trainer_notes` | Observaciones profesionales. Solo se agregan (triggers y permisos impiden editar o borrar) |
| access | `attendance` | Visitas: entrada, salida, método, excepción con motivo. Índice único parcial: una entrada abierta por alumno. Trigger: solo cambia al registrar la salida. Sin borrado |
| members | `notifications` | Avisos de WhatsApp por vencimiento: tipo, destinatario (+593…, formato validado), texto, estado (Pendiente/Enviando/Enviado/Con error), intentos, proveedor. Único por (membresía, tipo, destinatario) |
| members | `members` (WhatsApp) | `whatsapp_consent`, `whatsapp_consent_at`, `whatsapp_consent_by`. `commercial_settings`: `notifications_enabled`, `staff_alert_phones` |
| members | `members` (QR) | `qr_token_hash` (SHA-256) y `qr_token_issued_at`; índice único por gimnasio. El token nunca se guarda |
| billing | `tax_rates` | Tarifas de IVA globales con vigencia. Una sola abierta por tipo; sembradas: General 15 %, 0 %, no objeto y exento |

Restricciones en la base además de las de la aplicación: precio > 0, duración ≥ 1, vigencias coherentes, descuento > 0 (≤ 100 si es porcentaje), usos ≤ máximo, rangos de las políticas, nombre de plan único sin distinguir mayúsculas.
## Módulo de clases grupales (Fase 5, `ClassesDbContext`)
Migraciones propias, con su historial en `classes.__ef_migrations_history`. Mismo aislamiento que los demás módulos (RLS forzado por `tenant_id`, claves compuestas). Se migra después de Wellness porque apunta a `training.trainer_profiles`.

| Esquema | Tabla | Contenido |
|---|---|---|
| classes | `group_classes` | Definición de la clase: actividad, instructor (FK compuesta a `training.trainer_profiles`), sucursal (FK compuesta, inmutable), duración, capacidad, nivel, ventana de reserva, plazo de cancelación, lista de espera y elegibilidad. Único `(tenant_id, branch_id, lower(btrim(name)))`. CHECKs de rangos y coherencia. Trigger: gimnasio, sucursal y origen no cambian. Sin `DELETE` para el rol de la aplicación |
| classes | `class_series` | Regla de recurrencia semanal (días en máscara de bits, hora local de la sucursal, rango de fechas ≤ 366 días, instructor y capacidad). **Genera** sesiones concretas. FK compuesta a la clase con su sucursal. Trigger: la regla y el origen no cambian, solo se extiende y una serie cancelada no se reactiva. Sin `DELETE` |
| classes | `class_sessions` | Una fecha y hora concretas (`starts_at`/`ends_at` en UTC), instructor, capacidad, estado `Scheduled/Cancelled/Completed`, motivo de cancelación y, si se programó fuera de la disponibilidad del instructor, el motivo de la excepción. FK compuesta `(tenant_id, class_id, branch_id)` a la clase: la sesión no puede estar en otra sucursal. **`EXCLUDE USING gist (tenant_id, instructor_id, tstzrange(starts_at, ends_at))` donde no está cancelada**: un instructor no da dos sesiones a la vez. Único parcial `(tenant_id, series_id, original_starts_at)`: regenerar una serie no duplica, ni resucita una sesión movida o cancelada. Trigger: origen inmutable y una cancelación no se deshace. Sin `DELETE` |
| classes | `class_reservations` | Reserva de un alumno en una sesión: estado `Confirmed/Waitlisted/Offered/Cancelled/Expired` (asistió/no asistió es otro registro, bloque 6), origen `Member/Staff`, cancelación con motivo y marca de «fuera de plazo». FK compuestas a la sesión **con su sucursal** y al alumno (`members.members`). **Único parcial `(tenant_id, session_id, member_id)` mientras está viva**. **Disparador de capacidad**: bloquea la fila de la sesión y no deja ocupar más cupos (confirmadas + ofrecidas) que la capacidad ni reservar en una sesión que no está programada (códigos `FC001`/`FC002`). Otro disparador impide bajar la capacidad de una sesión por debajo de lo ocupado (`FC003`). La lista de espera tiene su tope por clase (`FC005`) y solo existe si la clase la admite (`FC004`); las invitaciones guardan `offered_at` y `offer_expires_at` (coherentes entre sí) y las transiciones válidas las fija otro disparador (de la fila solo se pasa a ofrecida; una confirmada solo se cancela). Trigger: origen inmutable y una cancelada o vencida no se reactiva. Sin `DELETE` |
| classes | `class_attendance` | Resultado (`Attended`/`NoShow`) de **una** reserva confirmada: único por reserva (registrar dos veces no duplica). FK compuesta a la reserva con su sesión, alumno y sucursal: no pueden apuntar a cosas distintas. Disparador: solo una reserva `Confirmed` de una sesión no cancelada que ya empezó (`FC006`/`FC008`). `gym_entry_id` enlaza, solo para contrastar, la entrada al gimnasio del mismo alumno y sucursal que coincide con la sesión (no se escribe nada en `access.attendance`). Origen inmutable; una reserva con asistencia no se cancela; una sesión completada no se reabre. Sin `DELETE` |
| classes | `inbox_notifications` | Aviso para un usuario (FK compuesta al usuario del mismo gimnasio): tipo, título, texto, referencias (`data` jsonb) y **clave anti-duplicado única por destinatario**; su entrega por canal (`InApp`, y reservados `Email/Sms/WhatsApp/Push` sin proveedor): estado `Pending/Delivered/Failed`, intentos, próximo intento, código del último error. Pendiente ⇔ tiene próximo intento; entregado ⇔ tiene fecha de entrega. Disparador: el contenido y el destinatario no cambian, lo leído no vuelve a no leído, lo entregado no cambia de estado. Sin `DELETE` |
| classes | `notification_preferences` | Preferencia por usuario y tipo de aviso (sin fila = activado) |
| classes | `idempotency_keys` | Respuesta guardada de cada solicitud con `Idempotency-Key` (clave por gimnasio): repetirla devuelve la misma reserva |

## Módulo de analítica (Fase 6, `AnalyticsDbContext`)
Casi todo lo que lee son **vistas de solo lectura** de las tablas de otros módulos (`ExcludeFromMigrations`): no crea ni modifica datos de negocio. Su única tabla propia es el registro de exportaciones. Migraciones propias, con su historial en `analytics.__ef_migrations_history` (`InitialAnalytics` → `AddAnalyticsSecurity`).

| Esquema | Tabla | Contenido |
|---|---|---|
| analytics | `report_exports` | Quién pidió una exportación a CSV, cuándo, qué reporte, con qué filtros (`jsonb`, sin contenido), cuántas filas y bytes, la clave del archivo en el almacén privado, cuándo vence (24 h), cuántas descargas lleva y cuándo se purgó. RLS forzado por gimnasio, FK compuesta `(tenant_id, requested_by)` al usuario. CHECK: formato `csv`, reporte conocido, 0–20.000 filas, vencimiento posterior a la solicitud, y «sin clave ⇔ purgado». Disparador: la solicitud no se modifica; solo avanzan el contador de descargas y la purga del archivo (y un archivo purgado no se recupera). El rol de la aplicación puede leer, insertar y actualizar, **no borrar**. |
