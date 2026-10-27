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
