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
| billing | `tax_rates` | Tarifas de IVA globales con vigencia. Una sola abierta por tipo; sembradas: General 15 %, 0 %, no objeto y exento |

Restricciones en la base además de las de la aplicación: precio > 0, duración ≥ 1, vigencias coherentes, descuento > 0 (≤ 100 si es porcentaje), usos ≤ máximo, rangos de las políticas, nombre de plan único sin distinguir mayúsculas.