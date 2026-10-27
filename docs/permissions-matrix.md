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
| Entrenador | No | `organization.read`, `branches.read` |
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
| `platform.taxes.manage` | Solo plataforma: registrar tarifas de IVA por decreto |

Endpoints nuevos: `GET/POST /members`, `GET/PUT /members/{id}`, `PATCH /members/{id}/status`, `GET/POST /membership-plans`, `GET/PUT /membership-plans/{id}`, `GET/POST /promotions`, `GET/PUT /promotions/{id}`, `GET/PUT /commercial-settings`, `GET /tax-rates/current`, `GET/POST /platform/tax-rates`, `GET/POST /memberships`, `GET /memberships/expiring`, `GET /memberships/{id}`, `GET /memberships/{id}/events`, `POST /memberships/{id}/renew|freeze|reactivate|cancel`, `GET/POST /payment-obligations`, `GET /payment-obligations/{id}`, `POST /payment-obligations/{id}/void`, `GET/POST /payments`, `GET /payments/{id}`, `GET /payments/{id}/receipt`, `POST /payments/{id}/confirm` `POST /payments/{id}/reverse`, `GET/POST /cash-registers`, `PUT /cash-registers/{id}`, `POST /cash-registers/{id}/open\|close\|movements`, `GET /cash-registers/{id}/movements\|sessions[/{sessionId}]` `GET /receivables/summary`, `GET /attendance`, `GET /attendance/{id}`, `POST /attendance/validate-access`, `POST /attendance/check-in`, `POST /attendance/check-out` , `POST /members/{id}/qr-token`, `PUT /members/{id}/whatsapp-consent`, `GET /notifications` y `GET/PUT /notification-settings`.