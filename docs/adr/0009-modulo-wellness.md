# ADR 0009 — Módulo Wellness (entrenamiento, nutrición, evaluaciones y progreso)

**Estado:** Aprobado el 2026-10-27 (Fase 4 en nuestra numeración; «FASE 3» del prompt del usuario). Se implementa por bloques; cada bloque marca aquí lo realmente hecho.

## Diagnóstico del repositorio (bloque 1)
| Necesidad de la fase | Situación | Acción |
|---|---|---|
| Perfil de entrenador / empleados | **No existe** modelo de empleados. Solo hay usuarios con el rol `Entrenador` (puede ver alumnos y asistencia) | Crear `TrainerProfile` ligado al usuario y asignaciones alumno–entrenador con historial |
| Alumno como usuario | **No existe** vínculo alumno–usuario; el rol `Alumno` no tiene permisos y el usuario demo `alumno.uio@` no apunta a ninguna ficha | Vínculo `Member.UserId` y endpoints «de mí» (bloque 2) |
| Archivos (fotos de progreso) | **No existe** almacenamiento; la foto del alumno es solo una URL | Puerto `IFileStore` + implementación en disco privado (bloque 8) |
| Alumnos, sucursales, permisos, auditoría, RLS por gimnasio, pruebas, componentes Angular | Existen | Se reutilizan; no se crea otra API ni otra base de datos |
| Roles `Entrenador`, `Nutricionista`, `Alumno` | Existen en las plantillas con permisos mínimos | Se amplían con los permisos nuevos |
| Gráficos, constructor de rutinas, editor de comidas | No existen | Componentes propios en SVG (sin dependencias nuevas ni problemas de CSP) |

## Decisiones (confirmadas por el usuario el 2026-10-27)
1. **Módulo nuevo `FitCore.Wellness`**, con su `DbContext`, esquemas propios (`training`, `nutrition`, `wellness`) y las mismas reglas de aislamiento (filtro de EF + RLS forzado + claves compuestas con `tenant_id`). La misma API y la misma base de datos. El vínculo alumno–usuario vive en el módulo de alumnos existente.
2. **Acceso del alumno:** botón «Dar acceso» en su ficha. Crea el usuario (rol `Alumno`, correo del alumno) con una **clave temporal que se muestra una sola vez** y que el alumno debe cambiar al entrar. Sin depender de correo; cuando exista proveedor de correo se reemplaza por invitación con enlace. El alumno solo accede a recursos cuyo dueño se resuelve **en el backend** a partir de su sesión: el identificador que llegue del frontend nunca sustituye esa validación.
3. **Datos sensibles** (plan nutricional, alergias y restricciones, evaluaciones físicas, objetivos de salud, fotografías): permisos propios que **no** reciben por defecto `Propietario` ni `AdministradorSucursal`. Van a `Nutricionista` (nutrición) y `Entrenador` (evaluaciones y rutinas, no nutrición). El propietario puede concederlos a un rol y ese cambio queda auditado. Se audita también la **lectura** de datos sensibles, sin registrar su contenido.
4. **Fotografías:** puerto `IFileStore` con implementación en un volumen privado (nunca público), servidas solo por la API tras autorización y comprobación de consentimiento; nombres aleatorios, tipo y tamaño validados, consentimiento y revocación registrados, borrado al revocar. Cambiar a S3 u otro almacenamiento no toca el resto del sistema.
5. **Catálogo base de ejercicios:** unos 40 ejercicios globales sin multimedia, redactados como **catálogo base sugerido, a validar con el entrenador del gimnasio**. Sin vídeos ni enlaces inventados. Los globales los administra plataforma; cada gimnasio crea los suyos.
6. **Entrenadores y nutricionistas:** un perfil profesional por usuario (`TrainerProfile`) para entrenadores; los planes nutricionales los elabora cualquier usuario con el permiso correspondiente, registrado como profesional responsable.

## Implementación del bloque 2a (acceso del alumno)
- **Vínculo:** `members.members.user_id` (único por gimnasio, clave foránea a `identity.app_users`). La cuenta es un usuario normal con el rol `Alumno`, que solo tiene el permiso `me.read`.
- **Clave temporal:** `POST /members/{id}/access` (permiso `members.access`) crea la cuenta con el correo del alumno y devuelve una clave de 14 caracteres **una sola vez** (respuesta `no-store`; no se guarda ni se audita). `POST …/access/reset` genera otra y cierra las sesiones; `PATCH …/access` {active} suspende o reactiva la cuenta. Desactivar al alumno desactiva su cuenta; reactivarla es una decisión explícita. Todo queda en la auditoría sin la clave.
- **Cambio obligatorio, aplicado en el servidor:** `AppUser.MustChangePassword`. Mientras sea verdadero la cuenta **no tiene permisos ni sucursales** (solo puede ver `/auth/me` y cambiar la clave), así que ningún recurso responde aunque el cliente lo intente. Cambiar la clave cierra todas las sesiones. El frontend lleva a `/change-password` desde cualquier ruta.
- **Portal «de mí»:** `GET /me/member|memberships|payments|attendance` (permiso `me.read`). El alumno se resuelve **solo desde la sesión** (`Member.UserId == usuario`); estos endpoints no reciben ningún identificador, y los del personal con el id de otro alumno responden 403. Devuelven DTO recortados (sin notas internas ni ids de personal).
- **Permisos sensibles (infraestructura):** `PermissionDefinition.Sensitive`. `Propietario` y los administradores no los reciben por defecto, y quien administra roles (`roles.manage`) puede concederlos a un rol sin poseerlos (`UserAccess.CanGrant`). Los permisos sensibles concretos llegan con cada módulo.
- **Límite conocido:** el correo del usuario es único en toda la plataforma (ASP.NET Identity): una persona no puede tener cuentas con el mismo correo en dos gimnasios.

## Implementación del bloque 2b (entrenadores)
- **Módulo `FitCore.Wellness`** (esquema `training`, `WellnessDbContext`, migraciones `InitialWellness` y `AddTrainersSecurity`): RLS forzado, claves compuestas con `tenant_id`, y los datos de otros módulos (alumnos, usuarios, sucursales, roles) se leen con vistas de solo lectura propias: **los módulos de negocio no se referencian entre sí** (lo vigila una prueba de arquitectura).
- **Perfil (`TrainerProfile`):** un usuario es entrenador una sola vez por gimnasio; especialidades (máx. 10, sin repetidos), certificaciones, biografía, fecha de incorporación y estado. Las sucursales del entrenador **son las de su usuario** (no se duplican). No se puede desactivar con alumnos asignados.
- **Disponibilidad:** franjas semanales (día, hora de inicio y fin, sucursal opcional); se reemplazan completas, sin solapes ni fin anterior al inicio (máx. 28). Cada entrenador gestiona la suya; la de otro exige `trainers.manage`.
- **Asignaciones con historial:** un alumno tiene **un solo entrenador activo** (índice único parcial en la base). Asignarlo a otro cierra la asignación anterior con su motivo y abre la nueva en una transacción; nada se borra y una asignación cerrada es inmutable (trigger). El entrenador debe trabajar en la sucursal del alumno y ambos deben estar activos. Un entrenador solo lista **sus** alumnos; los de otro exigen `trainers.manage`.
- **Observaciones profesionales (`trainers.notes`, sensible):** solo se agregan (trigger: ni se editan ni se borran) y **solo las lee y escribe el entrenador que tiene hoy asignado al alumno**; ni el propietario ni el administrador, aunque administren entrenadores. Al cambiar de entrenador, el anterior pierde el acceso y el nuevo ve el historial. Se audita el hecho (creación y lectura), nunca el texto.
- **Mínimo privilegio en el rol `Entrenador`:** deja de tener `members.read` y `attendance.read` (veían todo el gimnasio); trabaja con sus alumnos asignados mediante el módulo de entrenadores. El propietario también deja de recibir los permisos sensibles: la sincronización de arranque se los **retira** si los tenía (se corrigió un defecto por el que se los volvía a conceder en cada reinicio).

## Implementación del bloque 3 (biblioteca de ejercicios)
- **Un solo modelo, dos orígenes:** `training.exercises` con `tenant_id` nulo = **global** (catálogo base de plataforma) o con valor = del gimnasio. Campos: nombre, descripción, músculo principal y hasta 5 secundarios, tipo, equipamiento, dificultad, **modo de registro** (repeticiones y carga / solo repeticiones / tiempo: las rutinas y sesiones lo usarán para validar series, repeticiones, duración y carga), instrucciones paso a paso (máx. 20), errores comunes (máx. 10), contraindicaciones o advertencias, enlace https opcional y estado.
- **RLS por operación:** todos los gimnasios *leen* los globales y lo propio; solo el gimnasio dueño inserta o modifica lo suyo; **solo la plataforma** inserta o modifica globales; nadie borra (sin política ni permiso de DELETE). Una prueba verifica estas reglas directamente en la base, con el rol de la aplicación.
- **Nunca se borran:** desactivar un ejercicio lo oculta de las nuevas rutinas (`isActive=true`), pero las rutinas y sesiones históricas lo siguen mostrando; reactivarlo es posible.
- **El catálogo base no se edita desde un gimnasio** (`exercises.global_readonly`): se **duplica** (`POST /exercises/{id}/duplicate`, copia propia «(copia)», «(copia 2)»…) y se adapta la copia. Cambios en la copia no tocan el original.
- **Catálogo base (42 ejercicios):** texto propio, general y conservador, **sin vídeos ni enlaces** (una prueba lo comprueba) y marcado en pantalla como contenido sugerido que cada gimnasio debe validar con su entrenador. Se carga de forma idempotente al arrancar (solo agrega los que faltan por nombre). El sistema no incorpora multimedia propia: el enlace opcional es del usuario y debe ser https (también lo exige la base).
- **API:** `GET/POST /exercises`, `GET/PUT /exercises/{id}`, `PATCH /exercises/{id}/status`, `POST /exercises/{id}/duplicate` (permisos `exercises.read`/`exercises.manage`, que reciben propietario, administrador de sucursal y entrenador) y `/platform/exercises` (`platform.exercises.manage`) para el catálogo base. Búsqueda por nombre y filtros por músculo (principal o secundario), equipamiento, dificultad, tipo, origen y estado, con paginación.

## Principios del dominio (de las reglas del prompt)
- **Versiones inmutables:** una rutina o un plan nutricional se versiona; modificarlo crea una versión nueva y las sesiones y registros ya hechos conservan la versión con la que se hicieron.
- **Mediciones, no diagnósticos:** cada medición guarda unidad, fecha, fuente y método; lo estimado se marca como derivado. El IMC se muestra como indicador general (peso / estatura²) solo con estatura > 0; no se calcula grasa corporal sin método y datos válidos. El sistema no diagnostica ni sustituye una valoración clínica, y las estimaciones calóricas no se presentan como exactas.
- **Evaluaciones y registros históricos solo se agregan:** nada se sobrescribe.
- **Ejercicios usados no desaparecen:** desactivar un ejercicio no lo quita de rutinas ni sesiones históricas.
- **Objetivos:** configurables; no hay metas automáticas basadas solo en peso o apariencia. La adherencia mide cumplimiento registrado, no resultados fisiológicos.
- **Mínimo privilegio y aislamiento:** un entrenador solo ve a sus alumnos asignados (y de sus sucursales); nadie alcanza datos de otro gimnasio manipulando identificadores (404).

## Alcance excluido en esta fase
App móvil Flutter, facturación electrónica, nómina y contabilidad.

## Bloques
| # | Bloque | Estado |
|---|---|---|
| 1 | Diagnóstico y dependencias (este documento) | Hecho |
| 2a | Acceso del alumno: vínculo alumno–usuario, clave temporal con cambio obligatorio, endpoints «de mí», permisos sensibles | Hecho |
| 2b | Módulo `Wellness`: entrenadores, disponibilidad, asignaciones con historial y observaciones | Hecho |
| 3 | Biblioteca de ejercicios (globales y del gimnasio, catálogo base) | Hecho |
| 4 | Rutinas, asignaciones y sesiones (versionado) | Pendiente |
| 5 | Nutrición (planes versionados, asignaciones, registro de cumplimiento) | Pendiente |
| 6 | Evaluaciones físicas (IMC, historial, comparación) | Pendiente |
| 7 | Objetivos, seguimiento y adherencia | Pendiente |
| 8 | Fotografías y privacidad (`IFileStore`, consentimiento) | Pendiente |
| 9 | Integración Angular (panel del entrenador, panel del alumno, gráficos) | Pendiente |
| 10 | Pruebas, regresión y documentación | Pendiente |
