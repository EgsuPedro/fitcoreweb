# ADR 0006: Módulo Platform, identidad y capas de aislamiento

**Estado:** Implementado (Fase 2A)

## Módulo Platform
Organizaciones (tenants y sucursales), Identity, auditoría y outbox viven juntos en `FitCore.Platform` con **un solo `DbContext`** (`PlatformDbContext`) y esquemas `organizations`, `identity`, `audit` y `messaging`. Es un refinamiento del ADR 0001: son el núcleo del que dependen todos los demás módulos y comparten claves compuestas `(tenant_id, id)`, así que separarlos en contextos distintos solo añadiría fricción. Los módulos de negocio (Billing, Members…) sí tendrán su propio contexto y esquema.

## Autenticación
- ASP.NET Core Identity para usuarios, hash de contraseñas y bloqueo (5 intentos, 15 minutos). Contraseña de 10+ caracteres con mayúscula, minúscula y número.
- El login recibe `tenantSlug`, correo y contraseña. El correo es único **por gimnasio**.
- Access token JWT HS256 de 15 minutos con `sub` y `tid`. **Los permisos no viajan en el token:** se leen de la BD en cada solicitud, así que un cambio de rol, una desactivación o la suspensión del gimnasio surten efecto de inmediato (401 en la siguiente solicitud). Cuesta una consulta por solicitud; se cachea solo si las mediciones lo justifican.
- Refresh token opaco `{tenantId}.{secreto}`; en la BD solo se guarda el SHA-256 del secreto. El prefijo permite activar RLS antes de buscarlo.
  - Rotación en cada uso. Si se presenta un token ya rotado se revoca **toda la familia** (detección de robo).
  - Dos refresh simultáneos con el mismo token: concurrencia optimista con `xmin`; solo uno gana.
  - Web: cookie `HttpOnly`, `SameSite=Strict`, ruta `/api/v1/auth` (`useCookie: true`). Móvil: cuerpo de la respuesta.
- Límite de intentos por IP en login, refresh y logout.

## Autorización
- Política global: todo endpoint exige sesión activa salvo `AllowAnonymous` explícito. Un test falla si algún endpoint de `/api/v1` no declara su autorización y otro si uno de negocio no exige un permiso concreto.
- Permisos granulares (`branches.create`…) con `RequirePermission`. Los roles son plantillas que se copian a cada tenant y son editables por tenant (la edición llega en la Fase 2B).
- Alcance por sucursal: `user_branches` o un rol con `GrantsAllBranches`. Una sucursal o usuario fuera del alcance responde 404.
- Anti-escalada: nadie asigna ni modifica un rol con permisos que no posee, ni uno que abarque todas las sucursales si él no las tiene, ni se edita a sí mismo.

## Aislamiento entre gimnasios (cuatro capas)
1. El tenant sale solo del JWT (`TenantContextMiddleware`).
2. Filtro global de EF en toda entidad `ITenantOwned` y verificación en `SaveChanges` (`TenantViolationException`).
3. Row-Level Security en PostgreSQL con `FORCE`: `tenant_id = current_setting('app.tenant_id')`. Cada conexión fija `app.tenant_id` al abrirse. La aplicación se conecta con el rol `fitcore_app` (sin superusuario ni `BYPASSRLS`).
4. Claves foráneas compuestas `(tenant_id, id)` que impiden relacionar filas de gimnasios distintos.

Además: `audit.audit_logs` es append-only (el rol de la aplicación solo tiene `SELECT` e `INSERT`).

## Gestión de roles
`POST /roles`, `PUT /roles/{id}` y `DELETE /roles/{id}` exigen `roles.manage`.
- Solo se pueden conceder permisos que el actor posee, y `GrantsAllBranches` solo si el actor accede a todas las sucursales.
- No se puede modificar un rol cuyo alcance supere el del actor.
- Los permisos de plataforma (`platform.*`) nunca se conceden desde la API de un gimnasio.
- El rol **Propietario** es inmutable e indeleble, y siempre tiene todos los permisos de gimnasio: al arrancar, `DatabaseInitializer` se los agrega a los gimnasios existentes cuando el catálogo crece.
- Los roles de plantilla se pueden ajustar pero no renombrar ni eliminar; un rol asignado a usuarios no se elimina.
- Los cambios en roles y sus permisos quedan auditados y surten efecto en la siguiente solicitud de cada usuario.

## Pendiente y limitaciones conocidas
- `FindByIdAsync` de Identity y `Find/FindAsync` de EF leen por clave y **no aplican el filtro global**. El código no los usa y un test de arquitectura (`ForbiddenApiTests`) rompe el build si alguien los introduce; RLS lo cubriría igual.
- Particionado mensual de `audit_logs` (cuando el volumen lo pida).
- Las migraciones en producción deben ejecutarse como un paso de despliegue con el usuario propietario; `Database:AutoMigrate` es solo para desarrollo y pruebas.
