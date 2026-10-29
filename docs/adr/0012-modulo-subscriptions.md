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

Seguridad de datos: las tablas con `tenant_id` llevan RLS forzada, clave compuesta y sin `DELETE` para el rol de la aplicación; el equipo de plataforma opera entre gimnasios por una ruta explícita y acotada (nunca por un `tenantId` recibido del cliente, y sin tocar datos operativos de alumnos).

## Estados y transiciones
`Trialing → Active | Expired | Cancelled | Suspended` · `Active → PastDue | Cancelled | Suspended | Expired` · `PastDue → Active | Suspended | Expired | Cancelled` · `Suspended → Active (reactivar) | Cancelled` · `Expired → Active (renovar/reactivar) | Cancelled` · `Cancelled` es terminal para esa suscripción (se contrata una nueva). Cualquier otra transición se rechaza con 409. Cada transición ocurre en una transacción que actualiza la suscripción, los derechos vigentes (`tenant_entitlements`), el historial y la auditoría, con control de concurrencia (`xmin`).

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

## Notificaciones comerciales
Tipos: inicio de prueba, fin de prueba próximo, vencimiento próximo, pago confirmado, pago fallido, mora, cambio de plan, cancelación, suspensión, reactivación. `subscriptions.notices` con único anti-duplicado y entrega interna (portal). El `SubscriptionWorker` genera los avisos de calendario (anticipos configurables) de forma idempotente y reintenta los canales externos, que hoy no existen.

## Bloques de implementación
| # | Bloque | Estado |
|---|---|---|
| 1 | Diagnóstico y ADR 0012 | Hecho |
| 2 | Módulo, catálogo de planes con versiones, funciones y límites; `/platform/plans` | Hecho |
| 3 | Suscripciones, transiciones, historial, concurrencia y migración de los gimnasios existentes | Pendiente |
| 4 | Servicio de capacidades, derechos vigentes, disparadores de límites y modo de acceso | Pendiente |
| 5 | Renovaciones, cambios de plan, cancelaciones, prórrogas y vencimientos (`SubscriptionWorker`) | Pendiente |
| 6 | Cobros comerciales: facturas internas, pagos, intentos, reversiones; separación de pagos de alumnos | Pendiente |
| 7 | Panel global de plataforma: organizaciones, métricas, notas, auditoría | Pendiente |
| 8 | Portal del cliente («Mi suscripción») en Angular y avisos comerciales | Pendiente |
| 9 | Abstracción del proveedor de pagos y webhooks protegidos (sin proveedor real) | Pendiente |
| 10 | Pruebas completas, revisión de seguridad, documentación e informe de la fase | Pendiente |
