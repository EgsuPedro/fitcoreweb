# Guía de ejecución y validación manual

Para levantar el sistema en una máquina de desarrollo y comprobar a mano lo implementado hasta la Fase 4. Todo es local; las claves de abajo no se guardan en el repositorio.

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

**Fase 3 (pantallas comerciales)**, con el propietario `demo`:
1. *Alumnos → Nuevo alumno*: una cédula inválida se rechaza en el formulario; una válida guarda y aparece en la lista. En la ficha: editar, desactivar y *Generar código QR* (el código se muestra una sola vez).
2. *Caja → Ver caja → Abrir caja* con un saldo inicial. Sin caja abierta, el efectivo se rechaza.
3. En la ficha del alumno, *Inscribir alumno* con un plan: nace *Pendiente de pago* con su obligación (precio con IVA incluido, 15 %).
4. *Registrar pago* en efectivo: se emite el recibo (*Ver recibo* → Imprimir) y la membresía pasa a *Activa* con el primer pago. Una transferencia queda *Por verificar* hasta que alguien con permiso la confirme en *Cobros*.
5. *Asistencia*: escribir el código del alumno (o escanear su QR), *Validar acceso*, *Registrar entrada*; aparece en *Dentro ahora*. *Registrar salida* lo retira.
6. *Caja → Cerrar caja*: contar el efectivo, *Revisar y confirmar* (modal de la aplicación) y cerrar; queda en *Cierres anteriores*.
7. *Cartera*: deuda por antigüedad, por sucursal y principales deudores. *Membresías → Por vencer*: las que terminan en 3, 7, 15 o 30 días.
8. Con `recepcion.uio` o `cajero.gye` solo se ven las pantallas que su rol permite; las rutas restringidas llevan a *Acceso denegado*.

**Avisos de WhatsApp (emisor simulado)**, con el propietario `demo`:
1. *Notificaciones*: activar los avisos y escribir un teléfono del personal (p. ej. 0987654321). Un aviso se genera solo para membresías **activas, sin renovación** que terminen en 0–3 días.
2. En la ficha de un alumno con teléfono: *Registrar autorización* (consentimiento). Sin ella nunca se le escribe.
3. El Worker revisa cada 30 minutos (`Notifications:IntervalMinutes`) y también al arrancar: `docker compose … restart worker`. En el log aparece `[WhatsApp simulado]` (con el teléfono enmascarado) y en *Notificaciones* el historial con el texto exacto y su estado. **No sale ningún mensaje real.**

**Acceso del alumno al portal**, con el propietario `demo` (o `admin.uio@`):
1. Abra la ficha de un alumno **con correo** → *Acceso al portal del alumno* → *Dar acceso*. Se muestra su correo y una **clave temporal que aparece una sola vez**: cópiela y entréguesela.
2. Cierre sesión e inicie como ese alumno (gimnasio `demo`, su correo y la clave temporal): lo lleva a *Cambiar contraseña* y no puede ir a ninguna otra pantalla hasta cambiarla.
3. Tras cambiarla vuelve a iniciar sesión: ve *Hola, <nombre>* con su membresía, pagos y visitas, sin menú de administración. *Restablecer clave* y *Suspender acceso* están en la misma ficha.

**Entrenadores** (Fase 4, bloque 2b), con el propietario `demo`:
1. *Entrenadores*: ya existe *Entrenador Demo* (usuario `entrenador.uio@`) con un alumno asignado. *Habilitar entrenador* convierte a otro usuario en entrenador.
2. Abra su perfil: agregue franjas de disponibilidad (*Guardar disponibilidad*), asigne otro alumno (si ya tenía entrenador, el anterior queda en el historial: marque *Incluir asignaciones anteriores*) o quite uno con motivo.
3. Cierre sesión y entre como `entrenador.uio@demo.fitcore.test`: el menú es mínimo (sin *Alumnos* ni *Asistencia*), solo ve sus alumnos y, con *Observaciones*, escribe notas profesionales.
4. Vuelva como propietario y abra la ficha de ese alumno: ve *Entrenador del alumno* (historial) pero **no** las observaciones: solo las ve el entrenador asignado.

**Biblioteca de ejercicios** (Fase 4, bloque 3), con el propietario `demo`:
1. *Ejercicios*: 42 ejercicios del **catálogo base** (etiqueta *Catálogo base*, solo lectura). Pruebe los filtros (músculo, equipamiento, dificultad, tipo, origen) y *Ver* para las instrucciones, errores comunes y advertencias.
2. *Duplicar* un ejercicio base crea su copia editable en su gimnasio; *Nuevo ejercicio* crea uno desde cero (el enlace de vídeo, si lo pone, debe ser https).
3. *Desactivar* un ejercicio propio lo saca de las nuevas rutinas sin borrarlo. Un ejercicio base no se puede editar ni desactivar desde el gimnasio.
4. Como administrador de plataforma, *Catálogo de ejercicios* permite crear y editar el catálogo base para todos los gimnasios.

**Rutinas** (Fase 4, bloque 4), con `entrenador.uio@`:
1. *Rutinas → Nueva rutina*: agregue días y ejercicios de la biblioteca (series, repeticiones o tiempo, descanso). Guardar una edición crea una **versión nueva**; las asignaciones en curso conservan la suya.
2. Desde *Entrenadores → Seguimiento* de un alumno asigne la rutina. Entre como el alumno y use *Registrar sesión*: series con repeticiones y carga, o solo repeticiones, o tiempo según el ejercicio.
3. El entrenador ve la sesión, la evolución por ejercicio y puede comentar. Ni el propietario ni el administrador de sucursal ven observaciones, evaluaciones, objetivos ni fotos.

**Nutrición** (Fase 4, bloque 5), con `nutricionista.gye@`:
1. *Planes nutricionales → Nuevo plan* (comidas y alimentos). En la ficha del alumno, *Nutrición*: perfil alimentario con **alergias destacadas** y asignación del plan.
2. Como alumno: *Registrar comida* del día (cumplida, parcial u omitida). La adherencia se calcula con esos registros.
3. El entrenador no ve la pestaña de nutrición.

**Evaluaciones, objetivos y fotos** (Fase 4, bloques 6 a 8):
1. Como entrenador, en *Seguimiento* de un alumno: *Nueva evaluación* (peso, talla, perímetros); el IMC se calcula y se muestra siempre como indicador general, no como diagnóstico. Una evaluación no se edita: se **corrige** con otra que la referencia y un motivo.
2. *Objetivos*: cree uno con valor inicial, meta y fecha; el avance se ve en barra y gráfico. Un objetivo no se cierra solo, aunque alcance la meta: lo cierra una persona con su estado final y una nota opcional.
3. *Fotos*: sin consentimiento no se puede subir ninguna. El alumno (o el personal por él) registra el consentimiento; **si el alumno es menor de edad solo puede darlo el personal con el nombre del representante**. Suba una foto JPEG o PNG (máx. 8 MB): la imagen se muestra solo con sesión iniciada. *Retirar consentimiento* pide confirmación y **borra todas las fotos** del alumno.
4. Los archivos están en el volumen `fitcore_files` (`FileStorage:RootPath`). **No lo borre ni lo comparta**; inclúyalo en los respaldos junto con la base de datos.

**Seguridad rápida:**
- 5 contraseñas erróneas seguidas bloquean la cuenta 15 minutos (aunque luego se escriba la correcta).
- Desactivar un usuario desde "Usuarios" cierra su sesión en la siguiente solicitud.
- En Swagger: `POST /api/v1/auth/login` con `{"tenantSlug":"demo","email":"…","password":"…"}`, copiar `accessToken`, botón **Authorize** y probar endpoints. Con un usuario de otro gimnasio, pedir un id ajeno devuelve 404.

## 7. Pruebas automáticas
```powershell
dotnet test FitCore.sln                           # ~8 min en total; unitarias, arquitectura e integración (PostgreSQL real con Testcontainers; 4 colecciones en paralelo)
cd src/web; npm ci; npm run check:i18n; npm test   # claves de traducción es/en y unitarias de Angular
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
| Subir una foto falla en el servidor | Compruebe que `FileStorage:RootPath` existe y que el usuario de la API puede escribir en él (en Docker lo prepara el Dockerfile) |
| 429 al iniciar sesión | Límite de intentos por IP (20 por minuto en login) |
