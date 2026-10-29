# ADR 0012 — Módulo Subscriptions (administración SaaS, planes comerciales y suscripciones)

**Estado:** Decisiones confirmadas el 2026-10-29 (Fase 7 en nuestra numeración; «FASE 6» del prompt del usuario). Se implementa por bloques; cada bloque marca aquí lo realmente hecho.

## Vocabulario (para no confundir los dos dominios)
| Término | Qué es | Dónde vive |
|---|---|---|
| **Plan comercial / suscripción SaaS** | Lo que FitCore 360 le vende a un gimnasio | **`FitCore.Subscriptions`** (esquema `subscriptions`) — *nuevo* |
| **Plan / membresía de gimnasio** | Lo que un gimnasio le vende a un alumno (`members.membership_plans`, `members.memberships`) | `FitCore.Commercial` — **no se toca** |
| **Pago de alumno** | `billing.payments`, `billing.payment_obligations` | `FitCore.Commercial` — **no se toca** |
| **Cobro comercial SaaS** | Lo que el gimnasio le paga a FitCore 360 | `subscriptions.invoices`, `subscriptions.payments` — *nuevo* |

Prefijos de código, rutas y permisos: `subscriptions.*`, `/platform/plans`, `/my/subscription`, `/platform/billing`. Ninguna entidad nueva reutiliza tablas de membresías ni de pagos de alumnos.

## Decisiones confirmadas
1. **Módulo nuevo `FitCore.Subscriptions`.** Referencia a `Platform` (tenants, permisos, acceso), como `Commercial`, `Wellness` y `Classes`. Lee otros módulos solo por vistas `*Ref` de solo lectura.
2. **Capacidades por contrato compartido.** Las interfaces (`ICapabilityEvaluator`, códigos estables de funciones y límites) viven en `BuildingBlocks` para que Platform, Commercial, Classes y Analytics las consulten **sin referenciarse entre sí**. Ningún módulo compara el nombre de un plan.
3. **Los límites se hacen cumplir en PostgreSQL y se explican en la API.** Tabla `subscriptions.tenant_entitlements` (límites y funciones **vigentes** del gimnasio, escrita en la misma transacción que cualquier cambio de suscripción) + disparadores `BEFORE INSERT` con bloqueo asesor por gimnasio en `organizations.branches`, `identity.app_users` (usuarios internos) y `members.members`. Dos altas simultáneas no pueden pasar el tope. La API consulta antes y devuelve un 409/402 con el límite, el consumo y qué hacer. Mismo patrón que los cupos de clases (`FC00x`).
4. **Avisos comerciales propios.** `subscriptions.notices` con clave anti-duplicado, visibles como banner y en «Mi suscripción». Los canales externos (correo, SMS, WhatsApp) **no existen**: sin proveedor no se simula ningún envío; el puerto existe y un canal sin configurar deja el aviso en «Fallido: canal no disponible».
5. **Política de vencimiento:** 7 días de gracia (configurables por plataforma y por plan) y luego **modo restringido** (detalle abajo). La suspensión comercial no cierra la sesión ni impide los cobros a alumnos.
6. **Proveedor de pagos:** solo la abstracción (`IPaymentProviderGateway`) y el flujo manual verificable (registrar el pago, confirmarlo, auditarlo). Sin proveedor, credenciales y entorno de prueba **no se implementa ni se simula ninguno**; no se afirma cobro automático ni factura tributaria.
7. **Los gimnasios existentes no se rompen.** La migración crea el plan `legacy` (sin vencimiento, sin límites, no vendible) y una suscripción `Active` para cada gimnasio existente; el tenant `platform` queda fuera. Un gimnasio nuevo recibe, por defecto, una suscripción según la configuración (prueba gratuita del plan indicado, o `legacy` si no se indica).
8. **Fuera de la fase:** facturación electrónica del SRI (fase independiente), prorrateo implícito, MRR/ARR sin fórmula documentada, nómina y contabilidad.

## Diagnóstico del repositorio (bloque 1)
| Necesidad | Situación real | Acción |
|---|---|---|
| Tenants | `organizations.tenants` (`Tenant`: slug, nombre, `Status` Active/Suspended, zona horaria, moneda `USD`, idioma). Sin RLS (solo el equipo de plataforma la expone). `IAuditable` no aplica | Se **amplía** sin cambiarla: la suscripción referencia `tenant_id` |
| Alta de gimnasios | `POST /platform/tenants` (`TenantProvisioningService`, crea roles, propietario y sucursal base) | Tras el alta se crea la suscripción inicial |
| Suspender / activar | `POST /platform/tenants/{id}/suspend` y `/activate` cambian `Tenant.Status`; el login y el refresh rechazan gimnasios no activos | Se mantienen como **suspensión administrativa/de seguridad**. La **suspensión comercial** es un estado de la suscripción y no cierra la sesión: se distinguen en datos y en la interfaz |
| Plataforma | Gimnasio `platform` + rol `AdministradorPlataforma`; permisos `platform.*` (`tenants.manage`, `taxes.manage`, `exercises.manage`), solo asignables en ese tenant (se fuerza al sembrar) | Permisos nuevos `platform.plans.*`, `platform.subscriptions.*`, `platform.billing.*` con la misma regla |
| Autorización | `RequirePermission`, `AccessGuards`, `IAccessContext`, prueba de paridad de políticas | Se reutiliza; los endpoints del cliente exigen permisos de gimnasio nuevos (`subscription.read`, `subscription.manage`) |
| Alta de sucursales / usuarios | `CreateBranchHandler`, `CreateUserHandler` (Platform) | Consultan el servicio de capacidades + disparador |
| Alta de alumnos | `Member.Create` en `MemberHandlers` (Commercial) | Ídem |
| Exportaciones | `analytics.report_exports` (Fase 6) | Función `exports` del plan: se consulta al exportar |
| Funciones por módulo | Cada módulo tiene sus permisos de rol | La **función del plan** (¿el gimnasio la contrató?) y el **permiso del rol** (¿puede este usuario?) son dos controles distintos y ambos deben cumplirse |
| Auditoría | `AuditSaveChangesInterceptor` (entidades `IAuditable` → `audit_logs`) y outbox de eventos de dominio | Se reutiliza para planes, suscripciones y cobros |
| Avisos | `classes.inbox_notifications` es de Classes (alumnos/personal); `members.notifications` es WhatsApp de gimnasios | **No se reutilizan** (acoplarían un módulo operativo a la facturación SaaS): `subscriptions.notices` |
| Archivos | `IFileStore` | Comprobantes internos descargables (si se generan) |
| Worker | `OutboxProcessor`, `WaitlistWorker`, `NotificationWorker`, `ExpiryNotificationWorker` | Nuevo `SubscriptionWorker`: avisos de vencimiento, fin de prueba, gracia → restricción, vencimientos |
| Angular | Layout con menú por permisos, `ListState`, `Pager`, `Badge`, i18n es/en, `permissionGuard` | Pantallas de plataforma y «Mi suscripción» |
| Proveedor de pagos | **No existe ninguno configurado** | Solo abstracción |
| Facturación tributaria | No existe; el SRI es una fase aparte | Los documentos internos se rotulan «Comprobante interno, no es un comprobante tributario» |

## Modelo (esquema `subscriptions`)
- **`plans`** — `code` único, nombre, descripción, reglas de uso, `is_active`. Un plan **nunca se borra**: se desactiva (un plan inactivo no se asigna a suscripciones nuevas; las existentes siguen con él). *Ajuste del bloque 2:* no hay `is_sellable` (basta `is_active`; el plan `legacy` del bloque 3 nace inactivo) ni `current_version_id` (la vigente es la de mayor número; evita una referencia circular).
- **`plan_versions`** — `currency`, `period` (Monthly, Quarterly, Yearly), `price` `numeric(12,2)`, `trial_days`, `grace_days` (nulo = valor de plataforma). Cada cambio de precio, periodicidad, prueba, gracia, límites o funciones crea una **versión inmutable** (número consecutivo, bloqueo de la fila del plan para que ediciones simultáneas se atiendan en orden). Una suscripción apunta a la versión que contrató: **un cambio de precio no altera retroactivamente** a nadie.
- **`plan_limits`** (versión, código de límite, valor; nulo = ilimitado) y **`plan_features`** (versión, código de función). Códigos estables en el catálogo de código (`Capabilities`): límites `branches`, `internal_users`, `members`, `storage_mb`; funciones `members`, `branches`, `cash_payments`, `attendance`, `training`, `nutrition`, `classes`, `advanced_reports`, `exports`, `advanced_permissions`. Agregar un código **no** exige tocar la autorización.
- **`subscriptions`** — una **vigente** por gimnasio (único parcial), estado, versión del plan contratada, `price`, `currency`, `billing_period` (**copiados** al contratar), `started_at`, `current_period_start/end`, `trial_ends_at`, `next_due_at`, `grace_ends_at`, `cancel_at_period_end`, `cancelled_at`, `suspension_reason`, `pending_plan_version_id` (cambio programado), `xmin` como control de concurrencia.
- **`subscription_events`** — historial de lo ocurrido (creación, prueba, activación, cambio de plan, renovación, cancelación, suspensión, reactivación, prórroga). **El estado actual vive en `subscriptions`**; los eventos son el historial, no la fuente.
- **`subscription_plan_changes`** — cambios de plan pedidos/aplicados (de versión, a versión, efectiva desde, motivo, quién).
- **`invoices`** (documento interno de cobro) y **`payments`** — separados: importe, moneda, período cubierto, emisión interna, vencimiento, estado (Pending, Paid, Failed, Void, Refunded, PartiallyRefunded), referencia del proveedor, `confirmed_at`; único `(proveedor, referencia externa)` para idempotencia. **`payment_attempts`** guarda intentos y resultado sin datos de tarjeta.
- **`provider_events`** — eventos de proveedor recibidos (único por proveedor + id de evento) para webhooks idempotentes.
- **`tenant_entitlements`** — proyección vigente (límites y funciones del gimnasio, y el **modo de acceso**: Full / Grace / Restricted), mantenida en la misma transacción; la leen los disparadores y el evaluador.
- **`notices`** — avisos comerciales por gimnasio, único `(tenant, tipo, clave anti-duplicado)`.
- **`platform_notes`** — notas administrativas no sensibles por gimnasio.
- **`settings`** — valores de plataforma (días de gracia por defecto, anticipo de avisos).

Seguridad de datos: las tablas con `tenant_id` llevan RLS forzada y sin `DELETE` para el rol de la aplicación. Política de `subscriptions` y `subscription_events`: **leer** = el propio gimnasio **o** el gimnasio `platform` (función `subscriptions.is_platform_context()`); **escribir** = solo el contexto de plataforma. Un gimnasio no puede modificar ni crear su propia suscripción ni siquiera con una inyección SQL. Las operaciones de plataforma usan `IgnoreQueryFilters()` y el `tenantId` sale de la ruta, nunca del cuerpo; no tocan datos de alumnos.

### Bloque 3 — lo implementado
- **Dominio** `Subscription` (estado actual + copia de plan, versión, precio, moneda y periodicidad) y `SubscriptionEvent` (historial inmutable). Períodos en fechas **locales del gimnasio**, con fin exclusivo (el siguiente empieza el día en que termina el anterior; mes calendario con tope al último día). Una suscripción vigente (no cancelada) por gimnasio: único parcial `ux_subscriptions_current`.
- **Endpoints de plataforma:** `GET/POST /platform/tenants/{id}/subscription`, `GET …/subscription/history`, `POST …/subscription/activate|suspend|reactivate`, `GET /platform/subscriptions?status&planCode&search&dueWithinDays`. Los `POST /platform/tenants/{id}/suspend` y `/activate` existentes siguen siendo la **suspensión administrativa/de seguridad** (cierra el acceso); la comercial es `…/subscription/suspend` y **no** cierra la sesión.
- **Alta de gimnasios:** `POST /platform/tenants` acepta `planCode` y `startTrial`. Se valida el plan **antes** de crear nada (plan inexistente, inactivo o sin prueba → 422 y no queda un gimnasio a medias) y luego se abre la suscripción; sin plan, el gimnasio recibe el plan por defecto (`Subscriptions:DefaultPlanCode`, `legacy`). El punto de extensión es `ITenantProvisioningHook` (BuildingBlocks), para que Platform no conozca Subscriptions.
- **Plan `legacy`** (migración `SeedLegacyPlan`): precio 0, anual, todas las funciones, sin límites, inactivo (no se vende). Cada gimnasio existente (menos `platform`) recibe una suscripción `Active` en él. Un plan de precio 0 no genera cobros: su período se renueva solo (lo hará el proceso de vencimientos del bloque 5).
- **Reactivación:** vuelve al estado previo si el período sigue vigente; si ya terminó, queda `PastDue` con `GraceEndsOn = hoy + gracia` (la del plan o `Subscriptions:GraceDays`, 7).
- **Pendiente para el bloque 5:** renovar, cambiar de plan, cancelar (inmediata o al fin del período), prórroga y el proceso automático que pasa a mora y vence.

## Estados y transiciones
`Trialing → Active | Expired | Cancelled | Suspended` · `Active → PastDue | Cancelled | Suspended | Expired` · `PastDue → Active | Suspended | Expired | Cancelled` · `Suspended → Active | Trialing | PastDue | Cancelled` (reactivar devuelve a la situación previa a la suspensión; si el período ya terminó, queda en `PastDue` con una gracia nueva) · `Expired → Active (renovar) | Cancelled` · `Cancelled` es terminal para esa suscripción (se contrata una nueva). La misma tabla vive en el dominio (`SubscriptionTransitions`) y en un disparador de PostgreSQL. Cualquier otra transición se rechaza con 409. Cada transición ocurre en una transacción que actualiza la suscripción, los derechos vigentes (`tenant_entitlements`), el historial y la auditoría, con control de concurrencia (`xmin`).

## Reglas de períodos y renovación (explícitas)
- Período siguiente = `period_end` anterior (sin huecos ni solapes) + una periodicidad; **no hay prorrateo implícito**. Mensual = +1 mes calendario (con tope al último día), trimestral = +3, anual = +12.
- **Prueba gratuita:** `trial_days` del plan; termina en `trial_ends_at`. Si no se convierte (no hay pago confirmado de la primera factura), pasa a `Expired`; si se confirma el pago, a `Active` con su primer período pagado.
- **Cambio a plan superior:** efectivo **de inmediato**, mismo `period_end`, sin cobro adicional ni crédito (política explícita «sin prorrateo»; si se quiere prorrateo, será una política nueva documentada y probada). **Cambio a inferior:** se programa para el **fin del período**; si el consumo ya supera los límites nuevos se informa, no se borra nada y quedan bloqueadas las altas hasta ponerse por debajo.
- **Cancelación:** inmediata (estado `Cancelled`, modo restringido de solo lectura/exportación) o **al final del período** (`cancel_at_period_end`; sigue activa hasta el fin).
- **Prórroga administrativa:** extiende `period_end`/gracia con motivo obligatorio y auditoría.
- **Renovación manual:** el equipo de plataforma registra el pago (trazable) y se emite el período siguiente. **Renovación automática** solo existirá cuando haya proveedor configurado; hoy no la hay.
- **Vencimiento:** al pasar `period_end` sin pago confirmado → `PastDue` (gracia: `grace_ends_at = period_end + gracia`); pasada la gracia → `Expired`. Un pago confirmado reactiva y renueva.

## Política de acceso (modo de acceso del gimnasio)
| Modo | Cuándo | Qué se permite | Qué se bloquea |
|---|---|---|---|
| **Full** | Trialing, Active | Todo lo del plan, dentro de sus límites | Lo que excede el plan o el límite |
| **Grace** | PastDue (dentro de la gracia) | Todo (con avisos y banner) | Igual que Full |
| **Restricted** | Suspended (comercial), Expired, Cancelled | Iniciar sesión; consultar y **exportar** los datos propios; ver y pagar lo comercial («Mi suscripción»); **registrar cobros y asistencia de alumnos y operar la caja**; pedir reactivación | Crear sucursales, usuarios internos y alumnos nuevos; funciones premium (`advanced_reports`, `exports` salvo exportar los propios datos, `advanced_permissions`, etc.); cambios de configuración |
Nunca se eliminan ni desactivan datos por vencimiento. **Suspensión administrativa (`Tenant.Status`) ≠ suspensión comercial**: la primera es de seguridad y cierra el acceso; la segunda restringe, no cierra. La retención posterior a la cancelación (conservación, exportación, eliminación) queda como **política documentada**; no se programa ningún borrado automático.

## Permisos
- Plataforma (solo en el gimnasio `platform`): `platform.plans.read|manage`, `platform.subscriptions.read|manage`, `platform.billing.read|manage` (registrar/confirmar cobros, prórrogas), `platform.metrics.read`.
- Gimnasio: `subscription.read` (ver plan, consumo, avisos, facturas; propietario y administrador), `subscription.manage` (pedir cambio de plan, cancelar, pedir reactivación; propietario). El propietario **no** puede editar precios ni marcar pagos.
Las métricas del panel global son **agregados**: no exponen datos personales, clínicos ni financieros de alumnos.

### Bloque 4 — lo implementado
- **Contrato** en BuildingBlocks (`ICapabilityEvaluator`, `AccessMode`, `CapabilityErrors`); lo implementa Subscriptions. Códigos de error estables: `subscription.limit_reached` (409), `subscription.feature_unavailable` (403), `subscription.restricted` (403), `subscription.not_found` (403: gimnasio sin derechos registrados; falla cerrado). Los mensajes dicen el plan, el límite, el consumo y qué hacer.
- **Derechos vigentes** `subscriptions.tenant_entitlements` (modo de acceso, funciones, límites): los **deriva un disparador de PostgreSQL** de la suscripción actual del gimnasio (la no cancelada o, si no hay, la última), en la misma transacción. Modo: Trialing/Active → Full; PastDue → Grace; Suspended/Expired/Cancelled → Restricted. Una escritura directa también los actualiza. RLS: lee el propio gimnasio o plataforma; escribe solo plataforma.
- **Límites bajo concurrencia** (disparadores `BEFORE INSERT`, y al reactivar): `organizations.branches` (sucursales **activas**), `identity.app_users` (usuarios **internos activos**: la columna nueva `account_kind` separa al personal de los alumnos con acceso al portal, que **no** cuentan) y `members.members` (todos los alumnos registrados). Un bloqueo asesor por gimnasio y límite serializa las altas: dos simultáneas nunca pasan el tope (SQLSTATE `FC101`; `FC102` = suscripción restringida; la API los traduce a 409/403). Sin derechos registrados (alta del gimnasio en curso, gimnasio de plataforma) no se bloquea. El **almacenamiento no se mide** todavía: el límite se guarda y se muestra como «sin medir».
- **Dónde se consulta el servicio:** altas de sucursal (la primera no exige la función `branches`; las siguientes sí, y el límite siempre), de usuario y de alumno; y un **middleware único** (`FeatureGateMiddleware`) que mapea grupos de rutas → función, sin condiciones dispersas: `classes`, `training`, `nutrition` (incluye evaluaciones y objetivos), `extra_storage` (fotos de progreso), `advanced_reports` (`/analytics`, reportes), `exports`, `attendance`, `cash_payments` y `advanced_permissions` (solo escrituras de `/roles`; los roles de plantilla se consultan siempre). **Modo restringido:** las escrituras de `classes`, `training`, `nutrition`, `extra_storage` y `advanced_permissions` se congelan; sus consultas, los reportes, la **exportación**, los **cobros, la caja, las obligaciones y la asistencia de alumnos** siguen funcionando. Crear sucursales, usuarios y alumnos se bloquea en Restricted.
- **Consumo:** `GET /my/subscription/usage` (permiso `subscription.read`): funciones habilitadas, y por límite: tope, consumo, restante, si se alcanzó y si se mide.
- **Funciones declarativas (sin punto de control propio todavía):** `members` y `branches` se exigen en el alta; no hay rutas que dependan solo de ellas.
- **Pasar a un plan menor** no borra nada: lo ya creado queda y las altas se bloquean hasta bajar del límite.

### Bloque 5 — lo implementado
Endpoints de plataforma (`platform.subscriptions.manage`): `POST /platform/tenants/{id}/subscription/renew|change-plan|cancel|resume-cancellation|extend` y `POST /platform/subscriptions/run-lifecycle`. Las solicitudes del propio cliente (cambio, cancelación) llegan en el bloque 8; el cobro que confirma una renovación, en el 6.
- **Renovar** (desde Active, PastDue o Expired): el período nuevo **empieza donde terminó el anterior** (sin huecos ni solapes); si ya había vencido, o el período resultante ya pasó entero, **empieza hoy** (no se cobra por días sin servicio). Fin = inicio + la periodicidad en meses calendario (con tope al último día del mes; sin anclaje al día original, así que 31-ene + 1 mes = 28-feb y el siguiente empieza el 28). Una activa solo se renueva dentro de `Subscriptions:RenewalWindowDays` (30) antes de vencer. Con una cancelación programada no se renueva hasta retirarla.
- **Cambio de plan, sin prorrateo:** se compara el **precio por mes** (precio ÷ meses de la periodicidad). **Mejora o lateral → inmediato**: cambian precio, versión, funciones y límites (los derechos vigentes se actualizan en la misma transacción), el período y la fecha de vencimiento **no cambian** y no hay cobro ni crédito de la diferencia. **Baja → programada** para el fin del período (el cliente usa lo que ya pagó) y entra en vigor al renovar, que ya cobra el precio nuevo. En **prueba** siempre es inmediato. Un cambio programado nuevo reemplaza al anterior; una mejora posterior lo cancela. Se registra en `subscription_plan_changes` (de qué versión a cuál, tipo, estado Applied/Scheduled/Superseded, quién y cuándo).
- **Cancelar:** inmediata (→ Cancelled, modo restringido, los datos se conservan) o **al final del período** (sigue vigente con todo su acceso; el proceso la ejecuta ese día con el motivo dado; se puede retirar con `resume-cancellation`). La programada solo existe desde Trialing o Active.
- **Prórroga administrativa** (1 a 90 días, motivo obligatorio, auditada y en el historial): en prueba alarga la prueba; activa, alarga el período vigente y el próximo vencimiento; en mora, alarga la gracia.
- **Proceso de vencimientos** (`SubscriptionLifecycleRunner`; lo ejecuta el `SubscriptionWorker` cada `Subscriptions:LifecycleIntervalMinutes`, 15, y también `run-lifecycle`): idempotente, día local de cada gimnasio, una transacción por suscripción con `FOR UPDATE SKIP LOCKED` (dos instancias a la vez no duplican nada; un fallo no frena a las demás). Prueba terminada sin pago → Expired · período terminado con cancelación programada → Cancelled · período terminado de un **plan gratuito (precio 0) → se renueva solo** (aplicando una baja programada) · período terminado de un plan de pago → PastDue con la gracia del plan (o `Subscriptions:GraceDays`, 7) · gracia terminada → Expired. No toca suspendidas, vencidas ni canceladas.
- **No hay cobro automático:** sin proveedor de pagos, un plan de pago no se renueva solo; queda en mora hasta que se registre el pago (bloque 6) o se renueve a mano.

## Notificaciones comerciales
Tipos: inicio de prueba, fin de prueba próximo, vencimiento próximo, pago confirmado, pago fallido, mora, cambio de plan, cancelación, suspensión, reactivación. `subscriptions.notices` con único anti-duplicado y entrega interna (portal). El `SubscriptionWorker` genera los avisos de calendario (anticipos configurables) de forma idempotente y reintenta los canales externos, que hoy no existen.

## Bloques de implementación
| # | Bloque | Estado |
|---|---|---|
| 1 | Diagnóstico y ADR 0012 | Hecho |
| 2 | Módulo, catálogo de planes con versiones, funciones y límites; `/platform/plans` | Hecho |
| 3 | Suscripciones, transiciones, historial, concurrencia y migración de los gimnasios existentes | Hecho |
| 4 | Servicio de capacidades, derechos vigentes, disparadores de límites y modo de acceso | Hecho |
| 5 | Renovaciones, cambios de plan, cancelaciones, prórrogas y vencimientos (`SubscriptionWorker`) | Hecho |
| 6 | Cobros comerciales: facturas internas, pagos, intentos, reversiones; separación de pagos de alumnos | Pendiente |
| 7 | Panel global de plataforma: organizaciones, métricas, notas, auditoría | Pendiente |
| 8 | Portal del cliente («Mi suscripción») en Angular y avisos comerciales | Pendiente |
| 9 | Abstracción del proveedor de pagos y webhooks protegidos (sin proveedor real) | Pendiente |
| 10 | Pruebas completas, revisión de seguridad, documentación e informe de la fase | Pendiente |
