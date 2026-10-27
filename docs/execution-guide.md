# Guía de ejecución y validación manual

Para levantar el sistema en una máquina de desarrollo y comprobar a mano lo implementado hasta la Fase 2. Todo es local; las claves de abajo no se guardan en el repositorio.

## 1. Requisitos
.NET SDK 9, Node 24 y Docker Desktop encendido.

## 2. Configurar el entorno (una sola vez)
```powershell
Copy-Item infra/compose/.env.example infra/compose/.env
```
Edite `infra/compose/.env` y cambie **todos** los valores:

| Variable | Qué es |
|---|---|
| `POSTGRES_PASSWORD`, `APP_DB_PASSWORD` | Clave del propietario de la base y del rol limitado `fitcore_app` que usa la API |
| `JWT_SIGNING_KEY` | Secreto de firma de tokens, mínimo 32 caracteres aleatorios |
| `PLATFORM_ADMIN_EMAIL`, `PLATFORM_ADMIN_PASSWORD` | Administrador de plataforma (10+ caracteres con mayúscula, minúscula y número) |
| `POSTGRES_PORT`, `WEB_PORT` | Puertos del host (por defecto 5433 y 8081; use otros si están ocupados) |

El archivo `.env` está ignorado por git.

## 3. Levantar
```powershell
docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env up --build -d
docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env ps     # todo "Up"; postgres "healthy"
```
La API aplica las migraciones al arrancar (modo desarrollo). Para apagar sin perder datos: `docker compose ... stop`. **No use `down -v`**: borra la base de datos.

| Qué | Dirección |
|---|---|
| Aplicación web | http://localhost:8081 |
| Swagger UI (solo Development) | http://localhost:8080/swagger |
| Salud | http://localhost:8080/health/live y http://localhost:8080/health/ready |

## 4. Cargar datos de demostración
El script crea el gimnasio `demo` con dos sucursales y un usuario por rol. Es repetible. Usted elige la clave de los usuarios de demostración:

```powershell
$e = @{}; Get-Content infra\compose\.env | % { $p = $_ -split '=',2; if ($p.Count -eq 2) { $e[$p[0]] = $p[1] } }
./scripts/demo-seed.ps1 -PlatformEmail $e['PLATFORM_ADMIN_EMAIL'] -PlatformPassword $e['PLATFORM_ADMIN_PASSWORD'] -DemoPassword '<clave nueva, 10+ caracteres con mayúscula, minúscula y número>'
```

## 5. Usuarios para validar
Se inicia sesión con **gimnasio + correo + clave**.

| Gimnasio | Correo | Clave | Rol | Sucursales |
|---|---|---|---|---|
| `platform` | el de `PLATFORM_ADMIN_EMAIL` | la de `PLATFORM_ADMIN_PASSWORD` | Administrador de plataforma | — |
| `demo` | propietario@demo.fitcore.test | la de `-DemoPassword` | Propietario | todas |
| `demo` | admin.uio@demo.fitcore.test | ídem | AdministradorSucursal | Quito Norte |
| `demo` | recepcion.uio@demo.fitcore.test | ídem | Recepcion | Quito Norte |
| `demo` | cajero.gye@demo.fitcore.test | ídem | Cajero | Guayaquil Centro |
| `demo` | entrenador.uio@demo.fitcore.test | ídem | Entrenador | Quito Norte |
| `demo` | nutricionista.gye@demo.fitcore.test | ídem | Nutricionista | Guayaquil Centro |
| `demo` | contador@demo.fitcore.test | ídem | Contador | todas (solo lectura + auditoría) |
| `demo` | alumno.uio@demo.fitcore.test | ídem | Alumno | Quito Norte (sin permisos) |

## 6. Qué comprobar
**Plataforma** (gimnasio `platform`): ve solo "Inicio" y "Gimnasios" en el menú; puede crear, suspender y reactivar gimnasios. No ve usuarios ni sucursales de ningún gimnasio.

**Propietario**: menú completo. Crear una sucursal, un rol propio con algunos permisos y un usuario con ese rol; iniciar sesión con ese usuario y verificar que solo ve lo que el rol permite. Consultar la auditoría y ver ahí lo que acaba de hacer.

**Resultado esperado por rol** (verificado contra la API):

| Usuario | Sucursales que lista | Usuarios | Auditoría | Crear sucursal |
|---|---|---|---|---|
| propietario | UIO, GYE | sí | sí | sí |
| admin.uio | solo UIO | sí | no (403) | no (403) |
| recepcion.uio, entrenador.uio | solo UIO | no | no | no |
| cajero.gye, nutricionista.gye | solo GYE | no | no | no |
| contador | todas | no | sí | no |
| alumno.uio | no (403) | no | no | no |

**Seguridad rápida:**
- 5 contraseñas erróneas seguidas bloquean la cuenta 15 minutos (aunque luego se escriba la correcta).
- Desactivar un usuario desde "Usuarios" cierra su sesión en la siguiente solicitud.
- En Swagger: `POST /api/v1/auth/login` con `{"tenantSlug":"demo","email":"…","password":"…"}`, copiar `accessToken`, botón **Authorize** y probar endpoints. Con un usuario de otro gimnasio, pedir un id ajeno devuelve 404.

## 7. Pruebas automáticas
```powershell
dotnet test FitCore.sln                           # ~1,5 min: unitarias, arquitectura e integración (PostgreSQL real con Testcontainers)
cd src/web; npm ci; npm test                      # unitarias de Angular
cd ../../tests/e2e; npm ci; npx playwright install chromium
$env:E2E_BASE_URL = 'http://localhost:8081'       # usa el entorno ya levantado; las claves del administrador vienen de .env
npx playwright test
```
Las e2e crean sus propios gimnasios con nombres únicos; no tocan el gimnasio `demo`.

## 8. Problemas frecuentes
| Síntoma | Causa y solución |
|---|---|
| El puerto 5432 u 8081 está ocupado | Cambie `POSTGRES_PORT` o `WEB_PORT` en `.env` y repita `up -d` |
| La API no arranca: "Jwt:SigningKey" | `JWT_SIGNING_KEY` vacío o con menos de 32 caracteres |
| Login 401 con la clave correcta | Cuenta bloqueada 15 minutos, usuario desactivado o gimnasio suspendido |
| 429 al iniciar sesión | Límite de intentos por IP (20 por minuto en login) |
