# Matriz de permisos

Fuente de verdad: `Permissions.cs` (catálogo) y `RoleTemplates.cs` (plantillas). Si este documento y el código discrepan, manda el código; una prueba (`RoleManagementTests`) verifica que el Propietario recupere los permisos nuevos del catálogo.

## Catálogo

| Permiso | Módulo | Descripción |
|---|---|---|
| `platform.tenants.manage` | platform | Administrar gimnasios (solo equipo de plataforma). Nunca se concede desde la API de un gimnasio |
| `organization.read` / `organization.update` | organization | Ver / editar los datos del gimnasio |
| `branches.read` / `.create` / `.update` | branches | Ver, crear y editar sucursales |
| `users.read` / `.create` / `.update` | users | Ver, crear y editar usuarios (roles, sucursales y estado) |
| `roles.read` / `roles.manage` | roles | Ver roles y permisos / crear, editar y eliminar roles |
| `audit.read` | audit | Consultar la auditoría |

## Plantillas de rol (cada gimnasio recibe una copia editable)

| Rol | Todas las sucursales | Permisos |
|---|---|---|
| Propietario | Sí | Todos los de gimnasio (no `platform.*`). Inmutable e indeleble |
| AdministradorSucursal | No | `organization.read`, `branches.read`, `users.read/create/update`, `roles.read` |
| Recepcion | No | `organization.read`, `branches.read` |
| Cajero | No | `organization.read`, `branches.read` |
| Entrenador | No | `organization.read`, `branches.read`, `trainers.read`, `trainers.notes` (sin listado general de alumnos ni asistencia: trabaja con sus alumnos asignados) |
| Nutricionista | No | `organization.read`, `branches.read` |
| Contador | Sí | `organization.read`, `branches.read`, `audit.read` |
| Alumno | No | Ninguno (los permisos propios llegan con el módulo de alumnos) |
| AdministradorPlataforma | Sí | `platform.tenants.manage`, `organization.read`. Solo existe en el gimnasio `platform` |

Correspondencia con los roles del prompt de la Fase 1: SuperAdmin → AdministradorPlataforma; Owner → Propietario; Administrator → Propietario con permisos ajustados; BranchManager → AdministradorSucursal; Receptionist, Cashier, Accountant, Trainer, Nutritionist, Student → Recepcion, Cajero, Contador, Entrenador, Nutricionista, Alumno. Los permisos de caja, alumnos, entrenamiento, etc. se agregan cuando se implementen sus módulos (Fase 3 en adelante); hoy los roles operativos solo tienen lectura básica.

## Endpoints y permiso requerido

Todo endpoint exige sesión activa (política global) salvo los marcados como anónimos. Un test falla si algún endpoint de `/api/v1` no declara su autorización.

| Endpoint | Permiso |
|---|---|
| `POST /auth/login`, `/auth/refresh`, `/auth/logout` | Anónimo, con límite de intentos por IP |
| `POST /auth/change-password`, `GET /auth/me` | Solo sesión |
| `GET /system/info`, `GET /health/live`, `GET /health/ready` | Anónimo |
| `GET/POST /platform/tenants`, `POST /platform/tenants/{id}/suspend`, `/activate` | `platform.tenants.manage` |
| `GET /organization` / `PUT /organization` | `organization.read` / `organization.update` |
| `GET /branches`, `GET /branches/{id}` | `branches.read` |
| `POST /branches` / `PUT /branches/{id}` | `branches.create` / `branches.update` |
| `GET /users`, `GET /users/{id}` | `users.read` |
| `POST /users` | `users.create` |
| `PUT /users/{id}/access`, `PUT /users/{id}/status` | `users.update` |
| `GET /roles`, `GET /permissions` | `roles.read` |
| `POST /roles`, `PUT /roles/{id}`, `DELETE /roles/{id}` | `roles.manage` |
| `GET /audit-logs` | `audit.read` |

## Reglas que se aplican además del permiso
- **Alcance por sucursal:** `user_branches` o un rol con "todas las sucursales". Una sucursal o usuario fuera del alcance responde 404. La cabecera `X-Branch-Id` se valida contra ese alcance.
- **Anti-escalada:** nadie asigna ni concede lo que no posee; nadie edita su propio acceso; los permisos `platform.*` jamás se conceden desde un gimnasio; el rol Propietario no se modifica.
- **Efecto inmediato:** los permisos no van en el JWT; se leen de la base de datos en cada solicitud.

## Fase 3: permisos comerciales (en curso)
Detalle en el [ADR 0008](adr/0008-modulo-commercial.md). Todos se agregan al Propietario al arrancar. Las plantillas de rol se actualizan solo para gimnasios nuevos.

| Permiso | Para qué |
|---|---|
| `members.read/create/update/status` | Ver, registrar, editar y activar/desactivar alumnos (limitado a las sucursales del usuario) |
| `plans.read` / `plans.manage` | Ver planes y políticas comerciales / crear y editar planes. Quien no accede a todas las sucursales solo administra planes de las suyas |
| `promotions.read/manage/apply` | Ver / definir promociones (requiere acceso a todas las sucursales) / aplicarlas al inscribir o renovar |
| `commercial.settings.manage` | Cambiar las políticas del gimnasio (acceso con deuda, descuento máximo, días de vencimiento); requiere acceso a todas las sucursales |
| `memberships.read/create/renew/freeze/cancel` | Ver / inscribir / renovar / congelar y reactivar / cancelar membresías (limitado a las sucursales del usuario) |
| `obligations.read` / `obligations.create` / `obligations.void` | Ver / crear (otros conceptos) / anular obligaciones de pago |
| `payments.read` / `payments.create` / `payments.confirm` / `payments.reverse` | Ver pagos y recibos / registrar pagos y abonos / confirmar transferencias / revertir pagos (todo limitado a las sucursales del usuario) |
| `cash.read` / `cash.manage` / `cash.operate` | Consultar cajas, sesiones y movimientos / crear y configurar cajas y cerrar sesiones ajenas (supervisor) / abrir y cerrar la propia caja y registrar movimientos |
| `attendance.read` / `attendance.register` / `attendance.override` | Consultar la asistencia / registrar entradas y salidas y validar accesos (manual o por QR) / autorizar un acceso por excepción con motivo |
| `notifications.read` | Consultar la configuración y el historial de avisos de WhatsApp (cambiarla exige `commercial.settings.manage` y acceso a todas las sucursales; el consentimiento del alumno exige `members.update`) |
| `exercises.read` / `exercises.manage` | Ver la biblioteca (catálogo base y propios) / crear, editar, duplicar y desactivar los ejercicios del gimnasio. Propietario, administrador de sucursal y entrenador los reciben |
| `training.plans.read` / `training.plans.manage` | Ver / crear, modificar (versión nueva), duplicar y archivar rutinas. Propietario, administrador de sucursal y entrenador |
| `training.members.manage` | Asignar una rutina a un alumno y cerrar asignaciones (solo a alumnos a su alcance: entrenador asignado o `wellness.members.all`) |
| `training.member.read` | Ver la rutina vigente, el historial y las sesiones de un alumno (el alumno, solo las suyas) |
| `training.sessions.log` | Registrar sesiones de entrenamiento. Entrenador y alumno (no el administrador); comentar sesiones es del entrenador con `training.members.manage` |
| `nutrition.plans.read` / `nutrition.plans.manage` | **Sensibles.** Ver / crear, modificar (versión nueva), duplicar y archivar planes nutricionales. Solo `Nutricionista` por defecto |
| `nutrition.members.manage` | **Sensible.** Asignar un plan a un alumno y cerrar asignaciones |
| `nutrition.member.read` | **Sensible.** Ver el plan, las alergias y el cumplimiento de un alumno (cada lectura del personal se audita; el alumno ve solo lo suyo) |
| `nutrition.profile.manage` | **Sensible.** Registrar alergias, intolerancias y restricciones alimentarias (historial) |
| `nutrition.logs.record` | **Sensible.** Registrar el cumplimiento de las comidas (el alumno, las suyas; el nutricionista, por su alumno) |
| `wellness.assessments.read` | **Sensible.** Ver las evaluaciones físicas, compararlas y ver el progreso de un alumno (el alumno, solo las suyas). Entrenador (asignado) y Alumno; ni el propietario ni el administrador ni el nutricionista por defecto |
| `wellness.assessments.manage` | **Sensible.** Registrar y corregir evaluaciones físicas. Entrenador (de sus alumnos asignados) |
| `wellness.goals.read` | **Sensible.** Ver los objetivos de un alumno y su progreso (el alumno, solo los suyos). Entrenador (asignado) y Alumno |
| `wellness.goals.manage` | **Sensible.** Definir y cerrar objetivos. Entrenador (de sus alumnos asignados) |
| `wellness.photos.read` | **Sensible.** Ver las fotos de progreso de un alumno y su consentimiento (el alumno, solo las suyas). Entrenador (asignado) y Alumno |
| `wellness.photos.manage` | **Sensible.** Registrar o retirar el consentimiento y subir o borrar fotos (retirar borra todas). Entrenador (asignado) y Alumno (solo las suyas) |
| `platform.exercises.manage` | Solo plataforma: crear y editar el catálogo base de ejercicios (visible para todos los gimnasios, de solo lectura para ellos) |
| `trainers.read` / `trainers.manage` | Ver perfiles de entrenadores y listar los propios alumnos / habilitar entrenadores, editar perfiles, asignar y quitar alumnos, ver el historial de entrenadores de un alumno |
| `trainers.notes` (**sensible**) | Escribir y leer observaciones profesionales, solo del alumno que el entrenador tiene asignado hoy. No lo reciben Propietario ni AdministradorSucursal por defecto; quien administra roles puede concederlo a un rol |
| `wellness.members.all` | Ver el seguimiento de todos los alumnos de sus sucursales y no solo los asignados (se usa desde las rutinas, nutrición y evaluaciones) |
| `group-classes.read` / `group-classes.manage` | Ver las clases grupales y su configuración / crear, editar, activar y desactivar clases (solo de las sucursales a las que el usuario tiene acceso; las demás responden 404). Administradores de sucursal y propietario: ambos; recepción y entrenador: lectura |
| `class-sessions.read` / `class-sessions.manage` | Ver el horario (sesiones y series) / programar, reprogramar y cancelar sesiones y series. Mismo alcance por sucursal (fuera de ellas, 404). Administradores de sucursal y propietario: ambos; recepción y entrenador: lectura |
| `class-reservations.read` / `class-reservations.manage` | Ver las reservas y quién asiste a cada sesión / reservar y cancelar en nombre de un alumno (la cancelación fuera de plazo exige motivo y queda marcada). Solo en sesiones de sus sucursales (fuera de ellas, 404). Administradores de sucursal, recepción y propietario: ambos; entrenador: lectura. El alumno reserva y cancela lo suyo por `/me/class-reservations` con `me.read` |
| `class-attendance.read` / `class-attendance.manage` | Ver la lista de participantes de una sesión (confirmados, invitaciones pendientes y fila de espera) y el historial de asistencia / registrar y corregir la asistencia y cerrar la sesión. Administradores de sucursal, recepción y propietario: cualquier sesión de sus sucursales; el **entrenador solo registra y cierra las sesiones donde es el instructor** (las demás las ve pero responden 404 al registrar). El alumno ve su propio historial en `/me/class-attendance` con `me.read` |
| `inbox.use` | Usar la bandeja de avisos propia (leer, marcar como leídos, elegir qué avisos recibir). Siempre es la del usuario de la sesión. Todos los roles de plantilla lo tienen, incluido el alumno |
| `analytics.dashboard.read` | Entrar al dashboard y a los reportes (Fase 6). Cada sección se entrega solo si también tiene el permiso de su área; sin ese permiso la sección no viaja en la respuesta. Propietario, administrador de sucursal, recepción, cajero, entrenador y contador |
| `reports.financial.read` | Cobros, métodos de pago, saldos, caja y reversiones, solo de las sucursales del usuario. Propietario, administrador de sucursal (su sucursal), cajero (su sucursal), contador (todas) |
| `reports.members.read` | Alumnos, membresías, renovaciones, vencimientos y retención. Propietario, administrador de sucursal, contador |
| `reports.operations.read` | Asistencia al gimnasio y ocupación de clases de las sucursales del usuario. Propietario, administrador de sucursal, recepción |
| `reports.instructor.read` | Ocupación y asistencia de las propias sesiones como instructor, sin ingresos ni reportes de otros. Entrenador |
| `reports.compare` | Comparar sucursales entre sí, solo las que el usuario puede consultar. Propietario, administrador de sucursal, contador |
| `reports.export` | Exportar reportes a Excel (por defecto) o CSV y descargar las propias exportaciones; exige además el permiso del reporte exportado. Propietario, administrador de sucursal, contador |
| `branding.manage` | Cambiar o quitar el **logo del gimnasio** que aparece en el menú y en los libros de Excel de los reportes (`PUT|DELETE /tenant/branding/logo`). Propietario. Ver el logo y la marca (`GET /tenant/branding[/logo]`) exige `organization.read` (ver datos del gimnasio). La plataforma carga el logo de un gimnasio con `platform.tenants.manage` (`PUT /platform/tenants/{id}/logo`) |
| `members.access` | Dar, restablecer, suspender y reactivar el acceso de un alumno a su portal (administrador de sucursal; recepción no lo tiene) |
| `me.read` | Solo del rol `Alumno`: ver sus propios datos en el portal (`/me/*`) |
| `platform.taxes.manage` | Solo plataforma: registrar tarifas de IVA por decreto |
| `subscription.read` | Fase 7: ver el portal «Mi suscripción»: plan, período, consumo, funciones y límites, facturas internas, avisos y solicitudes propias (`GET /my/subscription`, `/usage`, `/plans`, `/invoices`, `/notices`, `/requests`). Propietario por defecto; el administrador de sucursal y el resto no |
| `subscription.manage` | Fase 7: solicitar cambios de plan, cancelación o reactivación de la suscripción propia (`POST /my/subscription/requests`, `…/requests/{id}/withdraw`; la plataforma las aprueba o rechaza con `platform.subscriptions.manage`). No permite editar precios, marcar pagos ni cambiar el estado comercial |
| `invoicing.read` | Fase 9: ver la configuración de facturación electrónica y su estado de preparación (`GET /invoicing/settings`). Nunca devuelve el certificado ni su contraseña. Propietario, administrador de sucursal, cajero y contador |
| `invoicing.configure` | Fase 9: guardar datos del emisor, puntos de emisión, ambiente del SRI y cargar o retirar el certificado de firma (`PUT /invoicing/issuer`, `POST|PUT|PATCH /invoicing/emission-points…`, `PUT|DELETE /invoicing/certificate`). Propietario y contador |
| `invoicing.issue` (vista previa `GET /invoicing/sources/obligations/{id}/preview` y emisión `POST /invoicing/invoices`, dentro de las sucursales de quien consulta; `invoicing.read` consulta y lista los comprobantes `GET /invoicing/invoices[/{id}]`), `invoicing.send` (firmar `POST /invoicing/invoices/{id}/sign` y ponerlo en la cola del SRI, reenviar uno devuelto o reactivar uno estacionado `POST /invoicing/invoices/{id}/submit`; el envío y la consulta de autorización los hace el Worker), `invoicing.void` (bloque 9: descartar un comprobante devuelto, no autorizado o sin enviar, `POST /invoicing/invoices/{id}/discard`; el resumen del panel `GET /invoicing/invoices/summary` usa `invoicing.read`), `invoicing.credit-note` (bloque 10: vista previa de lo acreditable `GET /invoicing/invoices/{id}/credit-preview` y emisión `POST /invoicing/credit-notes`), `invoicing.download` (descargar el XML, `GET /invoicing/invoices/{id}/xml`, y el RIDE en PDF, `GET /invoicing/invoices/{id}/ride`, con constancia en el historial), `invoicing.resend-email` (bloque 8: reenviar por correo el XML autorizado y el RIDE, `POST /invoicing/invoices/{id}/email`) | Fase 9: el resto reservados para los bloques de emisión, transmisión al SRI, anulación, notas de crédito, descarga de XML/RIDE y reenvío por correo. `issue`, `download` y `resend-email` también los tienen el administrador de sucursal y el cajero; el resto, propietario y contador |
| `platform.plans.read` | Solo plataforma (Fase 7): consultar el catálogo de planes comerciales de FIT360 (`GET /platform/plans`, `/catalog`, `/{id}`). No es el permiso `plans.read` de los gimnasios (planes de membresía de alumnos) |
| `platform.billing.read` | Solo plataforma (Fase 7): consultar facturas internas, pagos y el resumen de cobros comerciales (`GET /platform/billing/invoices`, `/payments`, `/summary`) |
| `platform.billing.manage` | Solo plataforma (Fase 7): emitir y anular facturas, registrar pagos, intentos fallidos y reembolsos (`POST …/subscription/invoices`, `/platform/billing/invoices/{id}/pay|attempts|void`, `/payments/{id}/refund`). Pagar y reembolsar exigen `Idempotency-Key` |
| `platform.organizations.read` | Solo plataforma (Fase 7): listar y consultar organizaciones clientes, su consumo agregado, notas y actividad (`GET /platform/organizations…`). Nunca datos de alumnos |
| `platform.organizations.manage` | Solo plataforma (Fase 7): agregar notas administrativas y medir el consumo (`POST /platform/organizations/{id}/notes`, `/usage/refresh`) |
| `platform.metrics.read` | Solo plataforma (Fase 7): métricas agregadas del producto SaaS (`GET /platform/metrics`): MRR contratado, cobrado, mora, conversión |
| `platform.subscriptions.read` | Solo plataforma (Fase 7): consultar la suscripción comercial de un gimnasio, su historial y el listado de suscripciones (`GET /platform/tenants/{id}/subscription`, `…/history`, `GET /platform/subscriptions`) |
| `platform.subscriptions.manage` | Solo plataforma (Fase 7): asignar un plan, activar una prueba, suspender y reactivar comercialmente (`POST …/subscription`, `…/activate`, `…/suspend`, `…/reactivate`). Distinta de `platform.tenants.manage`, que suspende administrativamente (cierra el acceso) |
| `platform.leads.read` | Solo plataforma (Fase 8): ver las solicitudes de contacto y demostración del sitio público (`GET /platform/contact-requests`). Nunca se concede desde la API de un gimnasio |
| `platform.leads.manage` | Solo plataforma (Fase 8): marcar como atendida o reabrir una solicitud (`PATCH /platform/contact-requests/{id}`) |
| `platform.plans.manage` | Solo plataforma (Fase 7): crear y editar planes comerciales (precio, periodicidad, prueba, gracia, límites y funciones; cada cambio comercial crea una versión) y activarlos o desactivarlos. Se concede por defecto al rol `AdministradorPlataforma` (todos los `platform.*`, incluidos los futuros) y no se puede conceder a roles de ningún gimnasio |

Endpoints nuevos: `GET/POST /members`, `GET/PUT /members/{id}`, `PATCH /members/{id}/status`, `GET/POST /membership-plans`, `GET/PUT /membership-plans/{id}`, `GET/POST /promotions`, `GET/PUT /promotions/{id}`, `GET/PUT /commercial-settings`, `GET /tax-rates/current`, `GET/POST /platform/tax-rates`, `GET/POST /memberships`, `GET /memberships/expiring`, `GET /memberships/{id}`, `GET /memberships/{id}/events`, `POST /memberships/{id}/renew|freeze|reactivate|cancel`, `GET/POST /payment-obligations`, `GET /payment-obligations/{id}`, `POST /payment-obligations/{id}/void`, `GET/POST /payments`, `GET /payments/{id}`, `GET /payments/{id}/receipt`, `POST /payments/{id}/confirm` `POST /payments/{id}/reverse`, `GET/POST /cash-registers`, `PUT /cash-registers/{id}`, `POST /cash-registers/{id}/open\|close\|movements`, `GET /cash-registers/{id}/movements\|sessions[/{sessionId}]` `GET /receivables/summary`, `GET /attendance`, `GET /attendance/{id}`, `POST /attendance/validate-access`, `POST /attendance/check-in`, `POST /attendance/check-out` , `POST /members/{id}/qr-token`, `PUT /members/{id}/whatsapp-consent`, `GET /notifications`, `GET/PUT /notification-settings`, `POST /members/{id}/access`, `POST /members/{id}/access/reset`, `PATCH /members/{id}/access`, `GET /me/member`, `GET /me/memberships`, `GET /me/payments`, `GET /me/attendance`, `GET /trainers`, `GET /trainers/me`, `GET/POST /trainers`, `GET/PUT /trainers/{id}`, `PATCH /trainers/{id}/status`, `GET/PUT /trainers/{id}/availability`, `GET/POST /trainers/{id}/members`, `POST /trainers/{id}/members/{memberId}/unassign`, `GET /members/{id}/trainer-assignments` y `GET/POST /members/{id}/trainer-notes`, `GET/POST /exercises`, `GET/PUT /exercises/{id}`, `PATCH /exercises/{id}/status`, `POST /exercises/{id}/duplicate` y `GET/POST /platform/exercises`, `PUT /platform/exercises/{id}`, `PATCH /platform/exercises/{id}/status`.
## Fase 8 — rutas públicas, asistente e invitaciones
**Rutas sin sesión** (`AllowAnonymous`, con límite de frecuencia por IP; ninguna devuelve datos privados de un gimnasio):

| Ruta | Para qué | Protecciones |
|---|---|---|
| `GET /public/plans`, `GET /public/plans/{code}` | Catálogo público (solo planes publicados, activos y no heredados) | `public-read` (120/min), sin identificadores internos ni versiones, 404 idéntico para inexistente/oculto/inactivo |
| `POST /public/registrations`, `…/verify-email`, `…/resend-verification` | Registro con confirmación de correo | `public-register` (10/min), respuesta siempre 202, tope por correo, precio/moneda rechazados, token de un solo uso |
| `POST /public/invitations/preview`, `…/accept` | Ver y aceptar una invitación de personal | `public-register`, el gimnasio sale del enlace, respuesta idéntica para enlace malo |
| `POST /public/contact`, `POST /public/demo-requests` | Formularios del sitio | `public-register`, campo trampa, 3/hora por correo, consentimiento versionado |
| `POST /auth/forgot-password`, `POST /auth/reset-password` | Recuperar la contraseña | `auth-recovery` (10/min), respuesta siempre 202, 3/hora por cuenta, no aplica al gimnasio de plataforma |

**Con sesión del gimnasio:**

| Ruta | Permiso |
|---|---|
| `GET /onboarding` | `organization.read` |
| `PUT /onboarding/profile`, `PUT /onboarding/branch`, `POST /onboarding/steps/{settings\|team}/{complete\|skip}`, `POST /onboarding/complete` | `organization.update` |
| `GET /invitations` | `users.read` |
| `POST /invitations`, `…/{id}/revoke`, `…/{id}/resend` | `users.create` (además, el rol y las sucursales se validan como al crear un usuario: no se puede dar más de lo que se puede conceder) |
| `PATCH /platform/plans/{id}/visibility` | `platform.plans.manage` |
