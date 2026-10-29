# Informe de la Fase 7 — Administración SaaS, planes comerciales y suscripciones

Corresponde a la «FASE 6» del prompt del usuario (nuestra numeración: 7). Módulo `FitCore.Subscriptions` (esquema `subscriptions`). Decisiones, reglas y detalle por bloque: [ADR 0012](adr/0012-modulo-subscriptions.md). Validación manual con datos: [guia-pruebas-fase-7.md](guia-pruebas-fase-7.md).

**Regla principal de la fase:** lo que FitCore 360 le vende a un gimnasio (suscripciones comerciales) es **otro dominio** que lo que el gimnasio le cobra a sus alumnos (`members.*`, `billing.*`), y ningún cambio comercial corrompe las operaciones de los gimnasios ni de otro gimnasio.

## Lo que NO existe (dicho sin rodeos)
- **No hay cobro automático.** Ninguna suscripción de pago se renueva sola: queda en mora hasta que la plataforma registra el pago (a mano o por el webhook del proveedor).
- **No hay proveedor de pagos real ni simulado.** Existe el puerto (`IPaymentProviderGateway`) y un verificador de webhooks firmados con un contrato propio de FitCore 360; ningún adaptador de una pasarela concreta. Sin secreto configurado, los webhooks se rechazan con 503.
- **No hay facturación tributaria.** Los documentos son «comprobantes internos de cobro»; la facturación electrónica del SRI es una fase independiente.
- **No hay avisos por correo, SMS ni WhatsApp.** Solo avisos internos en el portal y un banner.
- **No hay prorrateo.** Una mejora de plan entra de inmediato sin cobrar ni acreditar la diferencia; una baja se aplica al terminar el período.
- **El almacenamiento no se mide**: el límite se guarda y se muestra como «sin medir».
- No se guarda ningún dato de tarjeta (no hay columnas para ello; los textos con forma de número de tarjeta se rechazan).

## Qué se entrega
| Bloque | Contenido | Estado |
|---|---|---|
| 1 | Diagnóstico del repositorio y ADR 0012 | Hecho |
| 2 | Módulo, catálogo de planes con versiones, funciones y límites | Hecho |
| 3 | Suscripciones, transiciones, historial, concurrencia y migración de los gimnasios existentes | Hecho |
| 4 | Capacidades, derechos vigentes, límites en PostgreSQL y modo de acceso | Hecho |
| 5 | Renovación, cambio de plan, cancelación, prórroga y proceso de vencimientos (Worker) | Hecho |
| 6 | Cobros comerciales: facturas internas, pagos, intentos fallidos, reembolsos | Hecho |
| 7 | Panel global de plataforma: organizaciones, consumo, métricas, notas, actividad | Hecho |
| 8 | Portal «Mi suscripción», solicitudes del gimnasio y avisos comerciales | Hecho |
| 9 | Abstracción del proveedor de pagos y webhooks firmados e idempotentes | Hecho |
| 10 | Pruebas de aceptación, revisión de seguridad, documentación e informe | Hecho |

## Arquitectura y decisiones (confirmadas el 2026-10-29)
- **Módulo nuevo `FitCore.Subscriptions`**, que referencia a Platform como Commercial, Wellness y Classes; las pruebas de arquitectura vigilan que no dependa de Commercial, Wellness, Classes ni Analytics, ni de ASP.NET Core, y que su dominio sea puro.
- **Capacidades por contrato compartido** en BuildingBlocks (`ICapabilityEvaluator`, códigos estables de funciones y límites): ningún módulo compara el nombre de un plan.
- **Los límites se hacen cumplir en PostgreSQL** (disparadores con bloqueo asesor por gimnasio) y se explican en la API con un mensaje claro. Las altas simultáneas nunca superan el tope.
- **Política de vencimiento:** 7 días de gracia (configurables) y luego modo **restringido**. La suspensión comercial **no** cierra la sesión ni impide cobrar a los alumnos; la suspensión administrativa del gimnasio (`Tenant.Status`) es otra cosa y sí cierra el acceso.
- **Los gimnasios existentes no se rompen:** reciben el plan `legacy` (todas las funciones, sin límites, sin cobro, no vendible) con una suscripción activa.
- **Mínimo privilegio:** el equipo de plataforma ve contrato, cobros y conteos agregados; **nunca** datos de alumnos (RLS lo impide y hay pruebas contra la base).

## Planes y capacidades
- **Funciones** (11): `members`, `branches`, `cash_payments`, `attendance`, `training`, `nutrition`, `classes`, `advanced_reports`, `exports`, `advanced_permissions`, `extra_storage`.
- **Límites** (4): sucursales activas, usuarios internos activos (los alumnos con acceso al portal no cuentan), alumnos registrados y almacenamiento (sin medir). Todos se declaran siempre; `null` es «sin límite» explícito.
- **Planes:** el catálogo lo define la plataforma (los planes comerciales se crean desde `/platform/plans`; no se siembra ninguno salvo `legacy`). Cada cambio de precio, periodicidad, prueba, gracia, funciones o límites crea una **versión nueva**: quien ya contrató conserva la suya. Desactivar un plan no borra nada.

## Reglas de suscripción y vencimiento
- **Estados:** Trialing, Active, PastDue, Suspended, Expired, Cancelled. La tabla de transiciones vive en el dominio y se repite en un disparador de PostgreSQL.
- **Períodos:** fechas locales del gimnasio, fin exclusivo; el siguiente empieza donde termina el anterior; mensual = +1 mes calendario, trimestral = +3, anual = +12.
- **Prueba** → sin pago confirmado vence; con pago pasa a Active.
- **Vencimiento:** período terminado sin pago → PastDue con gracia; gracia terminada → Expired (modo restringido). Un plan de precio 0 se renueva solo.
- **Cambio de plan:** mejora inmediata; baja programada al renovar; no se borra nada y, si el consumo supera los límites nuevos, se bloquean las altas hasta bajar.
- **Cancelación:** inmediata o al fin del período. **Prórroga:** 1 a 90 días con motivo.
- **Renovación manual:** solo dentro de una ventana de 30 días antes del vencimiento (o ya vencida).

## Política de suspensión y acceso a datos
| Modo | Cuándo | Se permite | Se bloquea |
|---|---|---|---|
| Full | Trialing, Active | Todo lo del plan, dentro de sus límites | Lo que excede el plan |
| Grace | PastDue dentro de la gracia | Todo, con banner y avisos | Igual que Full |
| Restricted | Suspended, Expired, Cancelled | Iniciar sesión, consultar y exportar, «Mi suscripción», **cobros, caja y asistencia de alumnos**, pedir reactivación | Crear sucursales, usuarios y alumnos; escrituras de clases, entrenamiento, nutrición, fotos y permisos avanzados |

Nunca se borran ni desactivan datos por vencimiento. La retención posterior a una cancelación es una política documentada; no hay borrado automático.

## Estado de la integración de pagos
Ver «Lo que NO existe». Lo que sí: pagos registrados a mano con clave de idempotencia, importe y moneda exactos y referencia única; webhooks firmados (HMAC-SHA256 con marca de tiempo firmada, tolerancia de 300 s, comparación en tiempo constante, rotación de secretos) que confirman un pago, registran un cobro fallido o un reembolso; idempotencia por (proveedor, id de evento); lo que no concuerda se registra como rechazado y no cambia nada. La tarjeta solo se registra a través del proveedor.

## Migraciones (esquema `subscriptions`, historial propio)
`InitialSubscriptions` → `AddSubscriptionsSecurity` → `AddTenantSubscriptions` → `AddTenantSubscriptionsSecurity` → `SeedLegacyPlan` → `AddTenantEntitlements` → `AddEntitlementsSecurity` → `AddSubscriptionLifecycle` → `AddSubscriptionLifecycleSecurity` → `AddSubscriptionBilling` → `AddSubscriptionBillingSecurity` → `AddPlatformPanel` → `AddPlatformPanelSecurity` → `AddSubscriptionPortal` → `AddSubscriptionPortalSecurity` → `AddProviderEvents` → `AddProviderEventsSecurity`. En Platform: `AddAccountKind` (columna `account_kind` de usuarios, para no contar a los alumnos como personal).

**Tablas:** `plans`, `plan_versions`, `plan_features`, `plan_limits`, `subscriptions`, `subscription_events`, `subscription_plan_changes`, `tenant_entitlements`, `invoices`, `payments`, `payment_attempts`, `payment_refunds`, `platform_notes`, `tenant_usage_snapshots`, `subscription_requests`, `subscription_notices`, `provider_events`. Todas con `tenant_id` llevan RLS forzada; el rol de la aplicación no tiene `DELETE`; lo histórico (planes, versiones, facturas, pagos, intentos, eventos) está protegido por disparadores contra reescritura y borrado.

## Endpoints (bajo `/api/v1`)
- **Planes:** `GET|POST /platform/plans`, `GET|PUT /platform/plans/{id}`, `PATCH …/status`, `GET /platform/plans/catalog`.
- **Suscripciones (plataforma):** `GET|POST /platform/tenants/{id}/subscription`, `GET …/history`, `POST …/activate|suspend|reactivate|renew|change-plan|cancel|resume-cancellation|extend`, `GET /platform/subscriptions`, `POST /platform/subscriptions/run-lifecycle`.
- **Cobros (plataforma):** `POST …/subscription/invoices`, `GET /platform/billing/invoices[/{id}]`, `POST …/invoices/{id}/pay|attempts|void`, `GET /platform/billing/payments`, `POST …/payments/{id}/refund`, `GET /platform/billing/summary`, `GET /platform/billing/provider`, `GET /platform/billing/provider-events`.
- **Panel (plataforma):** `GET /platform/organizations[/{id}]`, `POST …/{id}/usage/refresh`, `GET|POST …/{id}/notes`, `GET …/{id}/activity`, `GET /platform/metrics`; `GET /platform/subscription-requests`, `POST …/{id}/approve|reject`.
- **Portal del gimnasio:** `GET /my/subscription`, `/usage`, `/plans`, `/invoices`, `/notices`, `POST /notices[/{id}]/read`, `GET|POST /requests`, `POST /requests/{id}/withdraw`.
- **Webhook:** `POST /webhooks/payments` (anónimo, autenticado por firma; 503 sin proveedor).

## Pantallas de Angular
Plataforma: `/platform/metrics`, `/platform/organizations` (+ detalle con acciones, factura, notas, historial y actividad), `/platform/plans`, `/platform/billing` (facturas, pagos, proveedor), `/platform/requests`. Gimnasio: `/my/subscription` (resumen, uso, planes, facturas, avisos, solicitudes) y un banner comercial. Alta de gimnasio con plan y prueba. Todo en español e inglés.

## Permisos nuevos
Plataforma: `platform.plans.read|manage`, `platform.subscriptions.read|manage`, `platform.billing.read|manage`, `platform.organizations.read|manage`, `platform.metrics.read` (solo existen en el gimnasio `platform`; ningún otro gimnasio puede obtenerlos). Gimnasio: `subscription.read` (propietario y administrador), `subscription.manage` (propietario). Ver [matriz](permissions-matrix.md).

## Pruebas ejecutadas (resultados reales, 2026-10-29)
| Suite | Resultado |
|---|---|
| Integración (PostgreSQL real con Testcontainers) | **655** correctas (la cifra incluye 2 pruebas de aceptación de este bloque) |
| Unitarias Subscriptions | 134 |
| Unitarias SharedKernel / Commercial / Wellness / Classes / Analytics | 13 / 144 / 131 / 57 / 42 |
| Arquitectura | 28 |
| Angular (vitest) | 93 |
| End-to-end (Playwright contra API + PostgreSQL reales) | 45 |
| `check:i18n`, `ng build`, `npm audit --omit=dev`, `dotnet list package --vulnerable` | limpios / 0 vulnerabilidades |

### Las 24 pruebas obligatorias del prompt
Cada una se comprueba contra la base de datos, no solo por el código de respuesta HTTP.
| # | Requisito | Dónde se prueba |
|---|---|---|
| 1 | Crear y actualizar planes | `SubscriptionPlanTests` (crear, plan gratuito, datos inválidos, código único, versiones) |
| 2 | Desactivar un plan sin borrar su historial | `SubscriptionPlanTests` (desactivar/reactivar; la base impide borrar o reescribir) y `SubscriptionSeparationTests` |
| 3 | Crear una suscripción para un tenant | `SubscriptionTests` (plan heredado, alta con plan y prueba, asignación) |
| 4 | Iniciar y finalizar una prueba | `SubscriptionTests` (activar), `SubscriptionLifecycleTests` (prueba que vence; proceso idempotente), `SubscriptionPortalTests` (aviso de prueba) |
| 5 | Cambiar de plan | `SubscriptionLifecycleTests` (mejora inmediata, baja programada, reemplazo, inválidos) y `SubscriptionPortalTests` (solicitud aprobada) |
| 6 | Renovar | `SubscriptionLifecycleTests` (ventana, vencida, concurrencia) y `SubscriptionBillingTests` (pago extiende el período) |
| 7 | Cancelar inmediata o al fin del período | `SubscriptionLifecycleTests` y `SubscriptionPortalTests` |
| 8 | Transiciones de estado | `SubscriptionTests`/`SubscriptionLifecycleTests` (la base rechaza transiciones inválidas por SQL) y unitarias |
| 9 | Vencimientos y gracia | `SubscriptionLifecycleTests` (mora con la gracia del plan, vencimiento, plan gratuito) y unitarias de períodos |
| 10 | Límites de sucursales, usuarios y alumnos | `SubscriptionLimitTests` |
| 11 | Operaciones concurrentes no superan límites | `SubscriptionLimitTests.Altas_simultaneas…` y `La_base_rechaza_el_exceso…` |
| 12 | Un tenant no accede a otra suscripción | `SubscriptionTests`, `SubscriptionBillingTests`, `SubscriptionPortalTests` (API y SQL bajo RLS) |
| 13 | Restringir el panel global | `PlatformPanelTests`, `AuthorizationTests`, `SubscriptionPlanTests` |
| 14 | Auditoría de cambios administrativos | `SubscriptionPlanTests`, `SubscriptionTests`, `SubscriptionBillingTests`, `PlatformPanelTests` (actividad) |
| 15 | No procesar dos veces un webhook | `SubscriptionProviderTests` (reenvío, otro id con la misma referencia, 8 entregas simultáneas) |
| 16 | Rechazar firma inválida | `SubscriptionProviderTests` (7 variantes con respuesta idéntica) y `SignedWebhookGatewayTests` |
| 17 | Rechazar importe o moneda distintos | `SubscriptionProviderTests`, `SubscriptionBillingTests` |
| 18 | Evitar duplicados de pagos | `SubscriptionBillingTests` (clave de idempotencia, referencia única, pagos simultáneos) y `SubscriptionProviderTests` |
| 19 | Un pago fallido no activa | `SubscriptionBillingTests` y `SubscriptionProviderTests` |
| 20 | Política de acceso tras el vencimiento | `SubscriptionLimitTests.En_mora…` y `Con_la_suscripcion_restringida…` |
| 21 | Operaciones permitidas durante la suspensión | `SubscriptionLimitTests.Con_la_suscripcion_restringida…`, `SubscriptionTests`, `SubscriptionPortalTests` |
| 22 | Preservar historial de precios y pagos | `SubscriptionTests` (cambio de precio no altera lo contratado), `SubscriptionBillingTests` (protegido por la base) y `SubscriptionSeparationTests` |
| 23 | Persistir y validar en PostgreSQL | todas las anteriores: consultas SQL, disparadores, restricciones y RLS |
| 24 | La facturación SaaS no altera los pagos de alumnos | `SubscriptionSeparationTests.Todo_el_ciclo_comercial…` (huella de las tablas de alumnos antes y después de un ciclo completo, también en otro gimnasio y a la inversa) |

## Revisión de seguridad
- **Autenticación y autorización:** todo endpoint exige sesión o declara `AllowAnonymous` (lo verifica `AuthorizationTests`); el único anónimo nuevo es el webhook, autenticado por firma. Cada endpoint de plataforma exige su permiso; los permisos `platform.*` solo se conceden en el gimnasio `platform`.
- **Aislamiento multi-tenant:** `tenant_id` sale del token, nunca del cuerpo ni de la ruta en el portal; filtros globales de EF más RLS forzada en todas las tablas; un gimnasio no puede crear ni modificar su suscripción ni siquiera con SQL (probado con el rol de la aplicación).
- **Mínimo privilegio:** la plataforma no lee tablas de alumnos (RLS) y mide el consumo contando filas desde el contexto del propio gimnasio; el rol de la aplicación no tiene `DELETE` en las tablas de la fase.
- **Webhooks:** firma HMAC con tiempo constante, marca de tiempo firmada y tolerancia, 401 uniforme, límite de tamaño y de peticiones por IP, idempotencia y concordancia exacta de importe y moneda, sin guardar el cuerpo ni datos de tarjeta.
- **Secretos:** el secreto del webhook viene de la configuración del servidor (`PAYMENT_WEBHOOK_SECRET`), nunca del repositorio; no se registra ni se devuelve; admite rotación.
- **Datos de tarjeta:** ninguna columna para ellos; `CardDataGuard` (Luhn) rechaza números en notas, referencias, motivos y mensajes; el medio Tarjeta solo existe vía proveedor (restricción en la base).
- **Inyección y exposición:** sin SQL concatenado (el único SQL crudo está parametrizado); DTOs explícitos; las alertas, avisos y facturas del gimnasio no incluyen notas ni motivos internos (hay pruebas).
- **Dependencias:** `dotnet list package --vulnerable` y `npm audit --omit=dev` sin hallazgos.
- **Auditoría:** acciones de planes, suscripciones, cobros, solicitudes y suspensión administrativa quedan en la auditoría y en el historial de la suscripción.

## Riesgos, limitaciones y tareas pendientes
- Sin cobro automático ni proveedor real; el primer proveedor requerirá su adaptador (firma y formato propios), credenciales y un entorno de pruebas.
- Un evento del proveedor rechazado por una condición temporal (p. ej. suscripción suspendida) no se reintenta con el mismo id: se registra el pago a mano.
- No hay conciliación contra el proveedor ni cobro saliente (enlaces de pago, cargos).
- Los avisos de calendario usan el día UTC; no hay canales externos.
- El almacenamiento no se mide; la foto de consumo del listado puede tener hasta una hora.
- El MRR es lo **contratado** (precio ÷ meses de las suscripciones de pago activas o en mora), no lo cobrado; ambos se muestran por separado.
- Retención de datos tras la cancelación: política documentada, sin borrado automático.
- Prorrateo: no existe; si se quiere, será una política nueva documentada y probada.
- La integración continua en GitHub nunca se ha verificado (no hay `gh` instalado en este equipo).
