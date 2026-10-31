# Modelo de base de datos

PostgreSQL 17. Un solo `DbContext` (`PlatformDbContext`) para el módulo Platform. Los módulos de negocio futuros tendrán su propio contexto y esquema. Nombres en `snake_case`.

## Esquemas y tablas

| Esquema | Tabla | Contenido | `tenant_id` | RLS |
|---|---|---|---|---|
| organizations | `tenants` | Gimnasio: nombre, slug único, estado, zona horaria, idioma, contacto | (es la raíz) | No |
| organizations | `branches` | Sucursal: código único por tenant, código de establecimiento SRI, ubicación, estado | Sí | Sí |
| organizations | `tenant_logos` | Constancia del logo de cada gimnasio (uno por gimnasio): clave del archivo en el almacén privado, tipo (`image/png` o `image/jpeg`), peso (≤ 1 MB), SHA-256 (versión), medidas (32–2000 px) y quién lo cambió. El archivo no está en la base. RLS forzada por gimnasio; CHECK de tipo, peso, medidas y hash; FK al gimnasio. | Sí | Sí |
| organizations | `pending_registrations` | Fase 8: solicitud de registro previa al gimnasio (correo, clave **hasheada** que se borra al terminar, nombre, identificador reservado, plan, versiones legales aceptadas, estado Pending/Provisioning/Verified/Expired/Superseded/Rejected/Failed, vencimiento 24 h). Sin RLS (aún no hay gimnasio); la API nunca la expone. | No | No |
| organizations | `trial_claims` | Fase 8: una prueba gratuita por propietario (correo normalizado único). FK al gimnasio. | (global) | No |
| organizations | `legal_acceptances` | Fase 8: aceptación de términos y de privacidad por usuario, con versión y fecha. RLS forzada por gimnasio. | Sí | Sí |
| organizations | `tenant_onboarding` | Fase 8: avance del asistente de configuración (cuándo se hizo u omitió cada paso, cuándo terminó). RLS forzada. | Sí | Sí |
| organizations | `contact_requests` | Fase 8: solicitudes de contacto y demostración del sitio público (nombre, correo, teléfono y gimnasio opcionales, mensaje ≤ 1000, versión de privacidad, estado New/Attended). Sin IP ni dispositivo. Se purgan a los 180 días. Sin RLS: solo las ve plataforma. | No | No |
| organizations | `tenants.setup_status` / `contact_email` / `contact_phone` / `country`, `branches.phone` / `hours` | Fase 8: estado de configuración (`PendingSetup`/`Ready`, independiente del estado administrativo y de la suscripción) y datos de contacto. | — | — |
| identity | `account_tokens` | Fase 8: tokens de un solo uso (confirmar correo, recuperar clave, invitación) guardados como **SHA-256**; propósito, sujeto, gimnasio, vencimiento, usado, revocado. Consumir es una sola sentencia atómica. Sin RLS (se consumen antes de saber el gimnasio). | Opcional | No |
| identity | `invitations` | Fase 8: invitaciones de personal (correo normalizado, rol, sucursales `uuid[]`, estado Pending/Accepted/Revoked, vencimiento 7 días). Una pendiente por correo y gimnasio. RLS forzada. | Sí | Sí |
| messaging | `emails` | Fase 8: cola de correo saliente con reintentos (Pending/Sending/Sent/Failed, intentos, siguiente intento, candado). Los cuerpos con enlaces se borran al enviar o fallar. Sin RLS: la procesa el Worker entre gimnasios. | No | No |
| subscriptions | `plans.is_public` | Fase 8: el plan se ofrece en el sitio público (explícito; ningún plan se publica solo). | — | — |
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

## Módulo de suscripciones SaaS (Fase 7, `SubscriptionsDbContext`, esquema `subscriptions`)
Lo que **FIT360 le vende a un gimnasio**; no tiene relación con las membresías ni los pagos de alumnos (`members`, `billing`). Historial de migraciones en `subscriptions.__ef_migrations_history` (`InitialSubscriptions` → `AddSubscriptionsSecurity`). Los planes son **globales** (sin `tenant_id`); lo que dependa de un gimnasio llevará `tenant_id` y RLS forzada.

| Esquema | Tabla | Contenido |
|---|---|---|
| subscriptions | `plans` | Plan comercial: código único (formato estable), nombre, descripción, reglas de uso, activo. Un plan **nunca se borra** (trigger y sin `DELETE` para el rol de la aplicación) y su código/origen no cambia. Control de concurrencia por `xmin`. |
| subscriptions | `plan_versions` | Condiciones **inmutables** de un plan: moneda, periodicidad (Monthly/Quarterly/Yearly), `price numeric(12,2)` (0 a 1.000.000), días de prueba (0–365), días de gracia (nulo = valor de plataforma; 0–90). Únicas `(plan, número)` y numeración consecutiva por disparador. Una suscripción apuntará a una versión: un cambio de precio crea otra versión y no altera a nadie. |
| subscriptions | `plan_features` | Funcionalidades habilitadas por versión (código del catálogo `Capabilities.Features`). |
| subscriptions | `subscriptions` | La suscripción comercial de un gimnasio: estado (Trialing, Active, PastDue, Suspended, Cancelled, Expired), **copia** del plan contratado (versión, código, nombre, precio, moneda, periodicidad), períodos en fechas locales (inicio, fin exclusivo, fin de prueba, próximo vencimiento, fin de gracia), cancelación y suspensión con su motivo. FK al gimnasio y a la versión del plan. **Una vigente por gimnasio** (único parcial `status <> 'Cancelled'`). CHECK de estados, fechas, suspendida⇒motivo, cancelada⇔fecha y motivo, prueba⇒fecha de fin. Disparadores: tabla de transiciones válidas, el gimnasio/inicio no cambian, nada se borra. **RLS forzada**: lee el propio gimnasio o `platform`; escribe solo `platform`. Control de concurrencia por `xmin`. |
| subscriptions | `invoices` | Factura **interna** de cobro de una suscripción (no tributaria): número `FC-AAAA-NNNNNN`, importe/moneda/plan copiados, período, emisión, vencimiento, estado (Pending, Paid, Failed, Void, Refunded, PartiallyRefunded), intentos y reembolsado. Única abierta por suscripción; CHECK de estados coherentes (pagada⇔fecha de pago, anulada⇔motivo, reembolso⇔estado); disparador de transiciones e inmutabilidad; nada se borra. RLS: lee el propio gimnasio o `platform`; escribe `platform`. |
| subscriptions | `payments` | Pago confirmado de una factura (todo o nada): importe, moneda, medio (BankTransfer, Cash, Other; Card solo con proveedor), referencia, `idempotency_key` única, proveedor y referencia externa (hoy nulos), reembolsado. Único por factura; referencia bancaria única; disparador que exige coincidir con la factura en gimnasio, importe y moneda. **Sin columnas de tarjeta.** |
| subscriptions | `payment_attempts` | Intentos de cobro rechazados (medio, referencia, motivo). Inmutable. |
| subscriptions | `payment_refunds` | Reembolsos de un pago (importe, motivo, `idempotency_key` única). Inmutable. |
| subscriptions | `subscription_plan_changes` | Cambios de plan de una suscripción: versión de origen y destino, tipo (Upgrade, Downgrade, Lateral), estado (Applied, Scheduled, Superseded), fecha de vigencia, quién y cuándo. Solo avanza de «programado» a «aplicado» o «reemplazado»; nunca se borra. Mismo RLS (lee el propio gimnasio o `platform`; escribe `platform`). |
| subscriptions | `subscription_events` | Historial inmutable (Created, TrialStarted, Activated, PastDue, Suspended, Reactivated, Expired, Cancelled) con origen, destino, actor y detalle. Mismo RLS; sin UPDATE ni DELETE. |
| subscriptions | `tenant_entitlements` | Derechos vigentes del gimnasio (modo de acceso Full/Grace/Restricted, funciones `text[]`, límites `jsonb` con `null` = sin límite), derivados por disparador de la suscripción actual. RLS: lee el propio gimnasio o `platform`; escribe solo `platform`. Los disparadores de límites (`trg_branches_plan_limit`, `trg_app_users_plan_limit`, `trg_members_plan_limit`) y `subscriptions.limit_for(...)` (bloqueo asesor por gimnasio y límite) viven en el esquema `subscriptions` pero actúan sobre `organizations.branches`, `identity.app_users` y `members.members`. |
| subscriptions | `platform_notes` | Notas administrativas no sensibles por gimnasio (máx. 1000 caracteres, sin datos de tarjeta). RLS: solo `platform` lee y escribe. |
| subscriptions | `tenant_usage_snapshots` | Foto con fecha del consumo agregado por gimnasio (sucursales, usuarios internos, alumnos: solo conteos). RLS: solo `platform`. |
| subscriptions | `subscription_requests` | Solicitudes del gimnasio sobre su suscripción (cambio de plan, cancelación, reactivación). Una pendiente por suscripción y tipo. RLS: lee e inserta (solo `Pending`) el propio gimnasio y puede retirarla; aprobar o rechazar es de `platform`; una resuelta es inmutable (FC110). |
| subscriptions | `subscription_notices` | Avisos comerciales internos (tipo, plan, fecha, número de factura; sin texto libre). Único `(tenant_id, dedupe_key)`. RLS: crea solo `platform`; el gimnasio lee los suyos y solo marca `read_at` una vez; sin borrado. |
| subscriptions | `provider_events` | Constancia de cada evento recibido del proveedor de pagos: único `(provider, external_event_id)` (idempotencia), tipo, factura, importe, moneda, resultado (`Applied`/`Rejected`/`Ignored`) y código. No guarda el cuerpo de la petición ni datos de tarjeta. RLS: solo `platform`; inmutable. |
| identity | `app_users.account_kind` | `Staff` o `Member` (alumno con acceso al portal); solo el personal cuenta para el límite de usuarios internos. Migración `AddAccountKind` (rellena `Member` según el rol de plantilla «Alumno»). |
| subscriptions | `plan_limits` | Límite cuantitativo por versión y código (`Capabilities.Limits`); `NULL` = sin límite. Todos los límites del catálogo se indican siempre: uno olvidado no es «ilimitado». |

## Módulo de facturación electrónica (Fase 9, `InvoicingDbContext`, esquema `invoicing`)
Historial de migraciones en `invoicing.__ef_migrations_history` (`InitialInvoicing`). Todas las tablas llevan `tenant_id`, RLS forzada (`tenant_isolation`) y auditoría; `fitcore_app` no tiene `DELETE`.

| Esquema | Tabla | Contenido |
|---|---|---|
| invoicing | `issuer_profiles` | Perfil tributario del gimnasio (uno por gimnasio): RUC (13 dígitos), razón social, nombre comercial, dirección matriz, régimen (General, RimpeEmprendedor, RimpeNegocioPopular), obligado a contabilidad, resoluciones de contribuyente especial y agente de retención, ambiente (Test/Production), tipo de emisión y correo de notificación. |
| invoicing | `emission_points` | Establecimiento (3 dígitos ≠ 000) + punto de emisión (3 dígitos ≠ 000) + dirección del establecimiento + sucursal opcional + activo. Único `(tenant_id, establishment_code, point_code)`. |
| invoicing | `signing_certificates` | Certificado de firma: `.p12` y contraseña **cifrados** (AES-256-GCM, nonce y contexto por gimnasio), datos públicos (titular, emisor, serie, huella, vigencia, RUC del titular), activo y fechas. Un activo por gimnasio (único parcial). Al retirarlo, un CHECK exige que el archivo y la contraseña queden vacíos. |

Fase 9, bloque 4 (migración `AddInvoices`): comprobantes electrónicos. Todas con `tenant_id` y RLS forzada; `fitcore_app` solo tiene SELECT/INSERT (y UPDATE en `invoices` y `emission_sequences`), nunca DELETE.

| Esquema | Tabla | Contenido |
|---|---|---|
| invoicing | `issuer_profiles.card_payment_method` | Tarjeta habitual del gimnasio (Credit/Debit) para el código de forma de pago del SRI. |
| invoicing | `invoices` | Comprobante: origen (tipo, id, referencia, sucursal), numeración (punto de emisión, establecimiento, punto, secuencial, ambiente), fecha, **clave de acceso de 49 dígitos** (única y verificada por CHECK contra el propio registro), copia del emisor y del comprador, totales (base, descuento, IVA, total, diferencia de redondeo) y estado. Únicos: (gimnasio, tipo de origen, origen, tipo de comprobante), clave de acceso y (gimnasio, punto, tipo, ambiente, secuencial). Disparador `invoices_guard`: solo cambia `status`/`updated_at` (FC130), solo por transiciones válidas y nunca a «firmado» sin su XML firmado (FC131); no se borra. |
| invoicing | `invoice_lines` / `invoice_taxes` / `invoice_payments` | Líneas (precio unitario, descuento, base, código y porcentaje de tarifa, IVA), totales por tarifa y formas de pago con su código del SRI. Inmutables (FC130). |
| invoicing | `invoice_documents` | Bloque 5: XML sin firmar (`Xml`), XML firmado (`SignedXml`) y, desde el bloque 6, la respuesta de autorización del SRI (`AuthorizedXml`) del comprobante: contenido, huella SHA-256 y tamaño. Único por (comprobante, tipo). Inmutable (FC130). RLS forzada; el rol de la aplicación solo lee e inserta. |
| invoicing | `invoices.authorization_number`, `authorized_at` | Bloque 6: número y fecha de autorización del SRI. Se fijan una sola vez y solo cuando el comprobante queda autorizado (CHECK y disparador); «autorizado» exige además el documento `AuthorizedXml`. |
| invoicing | `invoice_transmissions` | Bloque 6: cola de transmisión, una fila por comprobante en curso (se borra al terminar): próxima hora, intentos fallidos, consultas pendientes, primer intento, último intento, último error (código), concesión (`locked_until`) y si está estacionado. Mutable; la única tabla de facturación con DELETE para la aplicación. RLS forzada. |
| invoicing | `invoice_sri_attempts` | Bloque 6: constancia inmutable de cada intercambio con el SRI (operación, ambiente, resultado, estado del SRI, código de fallo, duración, mensajes del SRI en JSON). No guarda el XML. RLS forzada. |
| invoicing | `invoice_emails` | Bloque 8: un envío por correo del XML autorizado y el RIDE (destinatario, estado Pending/Sending/Sent/Failed, intentos, próxima hora, concesión, último error como código, quién lo pidió; nulo = automático). Cola con concesión; mutable; sin DELETE para la aplicación. RLS forzada. |
| invoicing | (cambios del bloque 10) | `invoices` ahora admite `document_type = CreditNote` (con `modified_invoice_id`, `modified_document_number`, `modified_issue_date`, `modification_reason`) y `invoice_lines.modified_line_id`; el índice único por venta es parcial (`status <> 'Discarded' AND document_type = 'Invoice'`). Disparadores `FC132`: la nota solo modifica una factura autorizada del mismo gimnasio y ambiente, y sus líneas no superan la base acreditada. La numeración de notas es una fila propia de `emission_sequences` (`document_type = CreditNote`). |
| invoicing | `invoice_events` | Historial inmutable (Created, y los cambios de estado de los siguientes bloques) con quién y cuándo. |
| invoicing | `emission_sequences` | Último secuencial por gimnasio, punto de emisión, tipo de comprobante y ambiente; se incrementa de forma atómica dentro de la transacción del comprobante. |

## Módulo de analítica (Fase 6, `AnalyticsDbContext`)
Casi todo lo que lee son **vistas de solo lectura** de las tablas de otros módulos (`ExcludeFromMigrations`): no crea ni modifica datos de negocio. Su única tabla propia es el registro de exportaciones. Migraciones propias, con su historial en `analytics.__ef_migrations_history` (`InitialAnalytics` → `AddAnalyticsSecurity`).

| Esquema | Tabla | Contenido |
|---|---|---|
| analytics | `report_exports` | Quién pidió una exportación (Excel o CSV), cuándo, qué reporte, con qué filtros (`jsonb`, sin contenido), cuántas filas y bytes, la clave del archivo en el almacén privado, cuándo vence (24 h), cuántas descargas lleva y cuándo se purgó. RLS forzado por gimnasio, FK compuesta `(tenant_id, requested_by)` al usuario. CHECK: formato `csv` o `xlsx`, reporte conocido, 0–20.000 filas, vencimiento posterior a la solicitud, y «sin clave ⇔ purgado». Disparador: la solicitud no se modifica; solo avanzan el contador de descargas y la purga del archivo (y un archivo purgado no se recupera). El rol de la aplicación puede leer, insertar y actualizar, **no borrar**. |
