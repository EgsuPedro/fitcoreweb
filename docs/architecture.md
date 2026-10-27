# Arquitectura

Resumen de las decisiones aprobadas. El detalle y la justificación están en `docs/adr/`.

## Visión
FitCore 360 es un SaaS multiempresa para administrar gimnasios y sus sucursales (Ecuador, USD, SRI). Monolito modular con Clean Architecture: un único despliegue de API y un Worker, con módulos de límites estrictos (ADR 0001). Cliente web Angular; app móvil Flutter más adelante.

## Estructura (y por qué difiere del prompt de la Fase 1)
El prompt sugería `FitCore360.Domain/Application/Infrastructure/API`. La Fase 0 aprobó un **monolito modular**, donde cada módulo contiene sus propias capas, en lugar de cuatro proyectos globales:

| Prompt | Implementación aprobada |
|---|---|
| Domain | `FitCore.Platform/Domain` (+ `FitCore.SharedKernel` para tipos comunes) |
| Application | `FitCore.Platform/Application` (+ `FitCore.Application.Core`: despachador y contratos) |
| Infrastructure | `FitCore.Platform/{Identity,Persistence,Security}` (+ `FitCore.Infrastructure.Core`) |
| API | `FitCore.Api` (host HTTP) y `FitCore.Worker` |
| Tests.Unit / Tests.Integration | `FitCore.SharedKernel.UnitTests`, `FitCore.Architecture.Tests`, `FitCore.Api.IntegrationTests`, más `tests/e2e` (Playwright) |

Los límites entre capas los verifica `FitCore.Architecture.Tests` (el build falla si se rompen). Las rutas de la API también difieren de la lista del prompt (p. ej. `/platform/tenants`, `/organization`, `/auth/me`); la tabla vigente está en `README.md` y `docs/permissions-matrix.md`.

## Decisiones principales
- **Despachador propio mínimo** (`IDispatcher`, `Result<T>`), sin MediatR (ADR 0004).
- **Multiempresa en cuatro capas** (tenant solo del JWT, filtro global de EF, RLS de PostgreSQL con `FORCE`, claves compuestas). Ver ADR 0002 y 0006 y `docs/database-model.md`.
- **Identidad:** ASP.NET Core Identity; JWT de 15 minutos (valida firma, emisor, audiencia y expiración) que no lleva permisos; refresh token opaco, guardado como hash SHA-256, con rotación y revocación de familia (ADR 0006).
- **Autorización:** política global que exige sesión; permisos granulares por endpoint; alcance por sucursal; anti-escalada. Matriz en `docs/permissions-matrix.md`.
- **Auditoría y outbox:** auditoría append-only; outbox transaccional procesada por el Worker.
- **Ecuador:** USD, SRI, pagos por transferencia manual primero (ADR 0003 y 0005).
- **Frontend:** Angular 22, sesión en memoria + cookie `HttpOnly` de refresh, español por defecto (ADR 0007).

## Auditoría: alcance y limitaciones
Se registra, por evento: tenant, usuario, acción (`Entidad.Creada/Actualizada…`, `Auth.LoginSucceeded`, `Auth.LoginFailed`…), entidad e id, valores antes/después (JSON, sin contraseñas ni hashes), IP, id de correlación y fecha UTC. El resultado se expresa en la propia acción (correcto/fallido/bloqueado); no hay una columna de resultado separada ni se auditan las solicitudes rechazadas por falta de permiso (403). Cubre altas y cambios de entidades del núcleo, eventos de seguridad (login correcto/fallido, bloqueo, cambio de clave, refresh reutilizado) y cambios de roles.

Garantías: el rol de la aplicación solo puede `SELECT` e `INSERT` en `audit.audit_logs` (no `UPDATE` ni `DELETE`), con RLS por tenant; la API solo ofrece lectura (`audit.read`).

Limitaciones conocidas:
- Un administrador de base de datos (propietario o superusuario) puede alterar o borrar registros; el mecanismo no es a prueba de manipulación frente a quien tenga acceso privilegiado a PostgreSQL.
- Las lecturas de datos no se auditan, solo los cambios y los eventos de seguridad.
- La tabla crecerá sin límite; hace falta particionado mensual y retención (ver hoja de ruta).

Estrategia de protección para producción: (1) credenciales de propietario fuera del alcance de la aplicación y de los desarrolladores; (2) enviar los registros a un almacenamiento externo de solo-anexado (WORM o servicio de logs) mediante la outbox; (3) cadena de hashes por tenant para detectar alteraciones; (4) copias de seguridad con retención fija; (5) alertas ante `DELETE`/`TRUNCATE` sobre el esquema `audit` (pgaudit).

## Procedimiento seguro del primer administrador de plataforma
No hay credenciales fijas en el código. El administrador se crea al arrancar solo si se definen **ambas** variables (se omite en silencio si falta alguna), y únicamente si todavía no existe:

```powershell
# Desarrollo: infra/compose/.env (ignorado por git)
PLATFORM_ADMIN_EMAIL=<correo>
PLATFORM_ADMIN_PASSWORD=<clave de 10+ caracteres con mayúscula, minúscula y número>
```
Equivalen a `Platform:AdminEmail` y `Platform:AdminPassword`. Pasos recomendados:
1. Generar una clave aleatoria larga y guardarla en un gestor de secretos.
2. Iniciar sesión con el gimnasio `platform`, **cambiar la clave** (`POST /auth/change-password`) y luego retirar `PLATFORM_ADMIN_PASSWORD` del entorno.
3. Crear los gimnasios desde `/platform/tenants`; cada alta crea su Propietario.

Limitación: la creación del administrador se ejecuta junto con la inicialización de `Database:AutoMigrate`, que es solo para desarrollo y pruebas. En producción, el primer despliegue debe ejecutarse una vez con ese modo (con el usuario propietario de la base) o crear el administrador con un paso de despliegue dedicado; separar este paso en un comando propio está en la hoja de ruta.

## Configuración obligatoria
La API falla al arrancar con un mensaje claro si falta `Jwt:SigningKey` o tiene menos de 32 caracteres (hay una prueba). Las cadenas de conexión, el secreto JWT y las credenciales del rol de aplicación se pasan por variables de entorno; `infra/compose/.env.example` solo contiene valores de ejemplo.

## Ambientes
- **Development:** OpenAPI en `/openapi/v1.json` y Swagger UI en `/swagger` (se puede autorizar con "Bearer <token>"); migraciones automáticas.
- **Production:** sin Swagger ni OpenAPI; migraciones como paso de despliegue; `Network:TrustedProxies` limita de quién se acepta `X-Forwarded-For`.
- Health checks: `/health/live` (el proceso responde) y `/health/ready` (incluye PostgreSQL).
