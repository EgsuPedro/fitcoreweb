# Guía de pruebas de la Fase 8 (validación manual)

Sirve para comprobar con sus propios ojos el sitio comercial, el registro, el correo, la recuperación de contraseña, el asistente de configuración y las invitaciones. Todo ocurre en su máquina: **los correos son reales pero no salen a internet**; llegan a Mailpit, un buzón de desarrollo.

## 0. Preparación
```powershell
# Desde la raíz del repositorio
docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env up -d --build
```
- Aplicación: <http://localhost:8081> · API: <http://localhost:8080> · **Buzón de correos (Mailpit): <http://localhost:8025>**
- Su administrador de plataforma es el de `infra/compose/.env` (`PLATFORM_ADMIN_EMAIL` / `PLATFORM_ADMIN_PASSWORD`). El gimnasio se escribe `platform`.
- Si repite el flujo muchas veces seguidas, suba `REGISTER_RATE_LIMIT` en `infra/compose/.env` (por defecto 10 por minuto por IP).

## 1. Publicar un plan (plataforma)
1. Entre como administrador de plataforma → **Planes comerciales** → **Nuevo plan** (por ejemplo «Prueba 14 días»: precio 25, prueba 14 días, funciones y todos los límites).
2. En la lista, pulse **Publicar**. Un plan nuevo **nunca** se publica solo; uno inactivo o el plan heredado no se puede publicar.
3. Abra <http://localhost:8081/planes> **sin sesión**: el plan aparece con su precio real. Oculte el plan y recargue: desaparece.

## 2. Sitio público
- Recorra Inicio, Funcionalidades, Planes, Nosotros, Preguntas frecuentes y Contacto. Funcionalidades marca como «Próximamente» lo que no existe (SRI, nómina, app móvil, WhatsApp, cobro con tarjeta).
- Cambie el idioma ES/EN y recargue. Pruebe con el celular o con el modo móvil del navegador (360 px): el menú se pliega y no hay desplazamiento horizontal.
- Con teclado: la primera tecla Tab muestra «Saltar al contenido»; las preguntas frecuentes se abren con Enter.
- **Ver el HTML prerenderizado** (lo que ve Google): `curl http://localhost:8081/planes` debe mostrar `<title>Planes y precios · FIT360</title>` y el texto, sin ejecutar nada. `curl http://localhost:8081/members` es la aplicación con `noindex`.

## 3. Contacto y demostración
1. En **Contacto** pruebe enviar vacío (errores), con correo inválido y sin marcar la privacidad.
2. Envíe uno válido. Responde «Recibimos su mensaje».
3. Entre como plataforma → **Solicitudes comerciales**: aparece. **Marcar atendida** / **Reabrir**.
4. Si definió `CONTACT_NOTIFY_EMAIL`, el aviso llega a Mailpit. Sin él (o sin SMTP) la solicitud igual queda en la pantalla.

## 4. Registro de un gimnasio
1. **Crear mi gimnasio** (`/registro`): elija el plan; escriba nombre, correo, nombre del gimnasio (el identificador se sugiere solo) y una contraseña (10+, mayúscula, minúscula y número). Acepte términos y privacidad.
2. Aparece «Revise su correo». En **Mailpit** llegó el mensaje con el enlace. El botón de reenvío espera 60 s.
3. Abra el enlace: «¡Su gimnasio está creado!». **Mire la barra de direcciones: el token ya no está.** Vuelva a abrir el mismo enlace: «ya se usó» (y ofrece pedir otro).
4. Pruebe registrar de nuevo el mismo identificador: se señala en su campo.
5. **Prueba una sola vez:** registre otro gimnasio con el mismo correo (también `dueno+otro@…`) y confirme: el sistema lo rechaza explicando que la prueba ya se usó, y **no** crea el gimnasio.

## 5. Iniciar sesión y configurar
1. **Iniciar sesión** desde el mensaje de éxito: el identificador ya está escrito.
2. Verá el aviso «Su gimnasio está pendiente de configuración» y el **Resumen de su gimnasio** (configuración 0 de 2, plan en prueba, invitaciones).
3. **Continuar configuración**: datos del gimnasio (con logo opcional) → sucursal principal con horario (apertura anterior al cierre) → reglas de acceso (se puede omitir) → equipo (se puede omitir) → resumen → **Terminar configuración**. Cierre sesión a mitad y vuelva: retoma donde quedó.
4. Al terminar, el aviso desaparece.

## 6. Recuperar la contraseña
1. Cierre sesión → **¿Olvidó su contraseña?** → identificador y correo → mensaje siempre igual (pruebe con un correo que no existe: misma respuesta, y no llega nada).
2. En Mailpit, abra el enlace (1 hora, un solo uso). Pruebe una contraseña débil: se rechaza y **el enlace no se gasta**. Ponga una válida.
3. Entra con la nueva; la anterior ya no sirve. Llega un aviso «Se cambió su contraseña».

## 7. Invitar al equipo
1. **Invitaciones** → **Invitar a una persona** → correo, rol y sucursales → **Enviar**. La invitación queda «Pendiente».
2. En Mailpit, abra el enlace en **otra ventana privada**: muestra el gimnasio y el rol; cree nombre y contraseña. Inicie sesión con ella: verá solo lo de su rol.
3. Pruebe **Reenviar** (espera de 1 minuto), **Revocar** (el enlace deja de servir) y una segunda invitación al mismo correo (rechazada).
4. **Límite del plan:** si el plan permite pocos usuarios internos, las invitaciones pendientes reservan plaza; al llegar al tope el sistema lo dice.

## 8. Qué NO debe pasar (si pasa, es un fallo)
- Que el registro cree un gimnasio antes de confirmar el correo, o que acepte un precio escrito por el navegador.
- Que la respuesta de «olvidé mi contraseña», registro o reenvío cambie según exista o no la cuenta.
- Que un enlace sirva dos veces, o después de vencer o revocarse.
- Que la persona invitada quede en otro gimnasio, o con más permisos que los del rol elegido.
- Que un gimnasio vea las solicitudes comerciales, las invitaciones o la configuración de otro.

## 9. Pruebas automáticas
```powershell
dotnet test FitCore.sln                                  # backend completo (PostgreSQL real en contenedores)
cd src/web; npx ng test --watch=false                    # pruebas unitarias del frontend
cd tests/e2e; $env:E2E_BASE_URL='http://localhost:8081'; npx playwright test   # navegador real (necesita el stack levantado, incluido Mailpit)
```
La trazabilidad de las 20 pruebas obligatorias está en el [informe de la Fase 8](informe-fase-8.md).
