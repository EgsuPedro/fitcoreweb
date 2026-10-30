# Plan de pruebas desde pantalla — Fase 8 (con datos)

Para probar a mano el sitio comercial, el registro, el correo, la configuración inicial, la recuperación de contraseña y las invitaciones. Cada caso trae **qué escribir** y **qué debe pasar**. Si algo no coincide, anótelo en la última columna y envíeme el número del caso.

## Antes de empezar
| Qué | Dónde / valor |
|---|---|
| Aplicación | <http://localhost:8081> |
| **Buzón de correos de prueba (Mailpit)** | <http://localhost:8025> — todo correo del sistema llega aquí en unos 5 segundos. Ningún correo sale a internet, así que puede usar direcciones inventadas. |
| Administrador de plataforma | gimnasio `platform`, correo y clave de `PLATFORM_ADMIN_EMAIL` / `PLATFORM_ADMIN_PASSWORD` en `infra/compose/.env` |
| Sistema levantado | `docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env up -d` |

**Datos de práctica ya cargados** (ejecutado el 2026-10-30; si los borra o quiere reiniciarlos, repita el comando):
```powershell
./scripts/fase8-planes-prueba.ps1 -PlatformEmail <PLATFORM_ADMIN_EMAIL> -PlatformPassword '<PLATFORM_ADMIN_PASSWORD>'
```
Deja exactamente estos planes:

| Código | Nombre | Precio | Prueba | ¿Público? | Se contrata por registro | Límites (sucursales / usuarios / alumnos) |
|---|---|---|---|---|---|---|
| `inicio-prueba` | Inicio | $29 / mes | 14 días | Sí | Sí | 1 / 3 / 100 |
| `gratis` | Gratis | $0 | — | Sí | Sí | 1 / 2 / 30 |
| `pro-ventas` | Pro | $79 / mes | — | Sí | **No** (solo por ventas) | 5 / 15 / 1000 |
| `oculto` | Plan oculto | $15 | 7 días | **No** | — | — |
| `retirado` | Plan retirado | $19 | 7 días | Se desactivó | — | — |

Datos que se usan en los casos (cámbielos si repite una prueba, para no chocar con lo ya creado):

| Dato | Valor |
|---|---|
| Propietaria | María Torres · `maria.torres@correo.test` |
| Gimnasio | Fitness Quito Norte → identificador sugerido `fitness-quito-norte` |
| Contraseña válida | `Gimnasio2026Ok` |
| Contraseñas inválidas | `gimnasio` (sin mayúscula ni número) · `Corta1A` (menos de 10) |
| Contraseña nueva (recuperación) | `Nueva2026Clave` |
| Personal invitado | `carlos.recepcion@correo.test` · `lucia.cajera@correo.test` · `pedro.contador@correo.test` |
| Contraseña de los invitados | `Invitado2026Ok` |
| Visitante del formulario | Jorge Andrade · `jorge.andrade@correo.test` · 0991234567 · Power Gym Cuenca |

---

## A. Sitio público (sin iniciar sesión; use una ventana privada)
| # | Qué hacer | Debe pasar | Resultado |
|---|---|---|---|
| A1 | Abrir `/` | Portada «Administre su gimnasio en un solo lugar», botones «Crear mi gimnasio» y «Solicitar una demostración», tarjetas de lo que ya existe. | |
| A2 | Recorrer el menú: Funcionalidades, Planes y precios, Nosotros, Preguntas frecuentes, Contacto | Cada página abre con su título (pestaña del navegador: «Funcionalidades · FIT360», etc.). | |
| A3 | En **Funcionalidades**, bajar al final | Bloque punteado «Próximamente» con SRI, nómina, app móvil, WhatsApp y cobro con tarjeta, **sin fechas**. Nada de eso aparece como disponible arriba. | |
| A4 | En **Planes y precios** | Solo **tres** planes: Gratis ($0), Inicio ($29/mes, «14 días de prueba gratis») y Pro ($79/mes). **No** aparecen «Plan oculto» ni «Plan retirado». Inicio y Gratis tienen «Empezar»; Pro tiene «Hablar con ventas». | |
| A5 | En Planes, pulsar «Hablar con ventas» en Pro | Abre Contacto con el mensaje ya escrito: «Quiero información sobre el plan pro-ventas.» | |
| A6 | En **Preguntas frecuentes**, abrir «¿Emite facturas electrónicas del SRI?» con el teclado (Tab hasta la pregunta y Enter) | Dice «Todavía no… sin fecha comprometida». | |
| A7 | Cambiar a **EN** (arriba) y recorrer 3 páginas | Todo el texto en inglés, sin textos raros tipo `site.home.title`. El título de la pestaña también cambia. Volver a ES. | |
| A8 | Abrir `/terminos` y `/privacidad` | Aviso amarillo «BORRADOR… pendiente de revisión legal» y «Versión: 2026-10-30-borrador». | |
| A9 | Pulsar Tab recién cargada la portada | Aparece «Saltar al contenido» arriba a la izquierda. | |
| A10 | Abrir una dirección que no existe, por ejemplo `/members` sin sesión | Lo lleva a Iniciar sesión (no a un error). | |

## B. Contacto y demostración
| # | Qué hacer | Debe pasar | Resultado |
|---|---|---|---|
| B1 | `/contacto?tipo=demo`, pulsar **Enviar** con todo vacío | Errores «Este campo es obligatorio» y «Debe aceptar la política de privacidad…». El motivo «Quiero una demostración» ya está marcado. | |
| B2 | Nombre `Jorge Andrade`, correo `esto-no-es-correo`, mensaje `corto`, teléfono `abc` | «Escriba un correo válido», «Escriba un teléfono válido» y «Escriba un mensaje de entre 10 y 1000 caracteres». | |
| B3 | Corregir: correo `jorge.andrade@correo.test`, teléfono `0991234567`, gimnasio `Power Gym Cuenca`, mensaje `Quiero una demostración para mis dos sucursales en Cuenca.`, marcar la privacidad, **Enviar** | «¡Gracias! Recibimos su mensaje.» | |
| B4 | Repetir B3 cuatro veces más con el mismo correo (recargue la página entre envíos) | Las 5 veces en total muestran «Gracias» (el límite no se avisa), pero en H1 solo habrá **3** solicitudes de ese correo. | |

## C. Registro de un gimnasio
| # | Qué hacer | Debe pasar | Resultado |
|---|---|---|---|
| C1 | `/registro` | Selector de plan con **Inicio** y **Gratis** (Pro **no** está: se contrata por ventas). | |
| C2 | Elegir Inicio. Nombre `María Torres`, correo `maria.torres@correo.test`, gimnasio `Fitness Quito Norte` | El identificador se llena solo con `fitness-quito-norte`. | |
| C3 | Contraseña `gimnasio`, sin marcar términos, **Crear mi gimnasio** | «La contraseña no cumple los requisitos» y «Debe aceptar los términos y la política de privacidad». No se envía. | |
| C4 | Cambiar el identificador a `-mal-formato-` | «Use 3 a 40 caracteres: minúsculas, números y guiones.» Restaurar `fitness-quito-norte`. | |
| C5 | Contraseña `Gimnasio2026Ok`, marcar términos, **Crear mi gimnasio** | Pantalla «Revise su correo» con `maria.torres@correo.test`. El botón de reenvío dice «Reenviar en N s» y está deshabilitado. | |
| C6 | Abrir Mailpit | Un correo «Confirme su correo — FIT360» para esa dirección, con un botón/enlace. | |
| C7 | Esperar 60 s y pulsar «Reenviar el correo» | Mensaje «Si hay un registro pendiente… enviamos un enlace nuevo». En Mailpit llega un **segundo** correo. | |
| C8 | Abrir el enlace del **primer** correo (el viejo) | «El enlace no es válido o ya se usó» (el reenvío lo invalidó) y ofrece pedir otro. | |
| C9 | Abrir el enlace del **segundo** correo | «¡Su gimnasio está creado!», identificador `fitness-quito-norte`, mensaje «Su plan Inicio empieza con 14 días de prueba gratis». **Mire la barra de direcciones: ya no tiene `token=`.** | |
| C10 | Volver a abrir ese mismo enlace | «El enlace no es válido o ya se usó». | |
| C11 | `/registro` otra vez: Inicio, correo `otra.persona@correo.test`, gimnasio `Fitness Quito Norte` (mismo identificador) | Al enviar: «Ese identificador ya está en uso. Elija otro.» **en el campo** del identificador. | |
| C12 | `/registro?plan=gratis` | El plan Gratis viene preseleccionado. | |

## D. Una sola prueba gratuita por propietario
| # | Qué hacer | Debe pasar | Resultado |
|---|---|---|---|
| D1 | Registrar otro gimnasio: plan **Inicio**, correo `maria.torres+gym2@correo.test`, gimnasio `Fitness Quito Sur` (`fitness-quito-sur`), clave `Gimnasio2026Ok` | Acepta y dice «Revise su correo» (no se avisa todavía). | |
| D2 | Abrir su enlace en Mailpit | «Este correo ya aprovechó la prueba gratuita. Escríbanos desde la página de contacto…». **No** se crea el gimnasio (intente iniciar sesión con `fitness-quito-sur`: no existe). | |
| D3 | Repetir con plan **Gratis**, correo `maria.torres+gym3@correo.test`, gimnasio `Fitness Quito Centro` | Sí se crea (el plan Gratis no usa la prueba). | |

## E. Iniciar sesión, resumen y configuración inicial
| # | Qué hacer | Debe pasar | Resultado |
|---|---|---|---|
| E1 | En el éxito de C9 pulsar **Iniciar sesión** | El gimnasio ya viene escrito: `fitness-quito-norte`. Entrar con `maria.torres@correo.test` / `Gimnasio2026Ok`. | |
| E2 | Mirar el inicio | Aviso «Su gimnasio está pendiente de configuración…» y el **Resumen de su gimnasio**: «0 de 2 pasos obligatorios hechos», plan **Inicio** con «Prueba gratuita hasta …», «0 invitaciones pendientes». | |
| E3 | **Continuar configuración** → paso «Su gimnasio»: correo `esto-no-es-correo` → **Guardar y continuar** | «Revise los campos marcados.» | |
| E4 | Completar: nombre `Fitness Quito Norte`, correo `info@fitnessquito.test`, teléfono `+593 99 876 5432`, país `EC`, zona `America/Guayaquil`, idioma Español. Subir un logo PNG o JPG (cualquiera de más de 32 px) | Guarda y pasa a «Su sucursal principal». El logo aparece en la vista previa. | |
| E5 | **Recargar la página (F5)** | Vuelve al paso de la sucursal (el avance quedó guardado). | |
| E6 | Sucursal: nombre `Sede Norte`, dirección `Av. Eloy Alfaro N35-12 y Portugal, Quito`, teléfono `02 245 6789`. En **Lunes**: abre `22:00`, cierra `06:00` → Guardar | «En los días abiertos la apertura debe ser anterior al cierre.» (el día se marca en rojo). | |
| E7 | Dejar Lunes en `06:00`–`21:00`, Sábado `08:00`–`14:00`, Domingo «Cerrado» → Guardar | Pasa a «Reglas de acceso con deuda». | |
| E8 | **Cerrar sesión** y volver a entrar | El aviso sigue; «Continuar configuración» lleva al paso de reglas de acceso. | |
| E9 | Elegir «Exigir pago completo» → Guardar y continuar | Pasa a «Su equipo». | |
| E10 | «Omitir por ahora» en Equipo | Pasa al resumen: muestra nombre, sucursal, y «Reglas de acceso: Hecho», «Equipo: Omitido (puede hacerlo después)». | |
| E11 | **Terminar configuración** | «¡Listo! Su gimnasio quedó configurado.» Al ir al inicio el aviso amarillo **desaparece** y la tarjeta dice «Completa». | |
| E12 | Ir a **Sucursales** | Existe la sucursal «Sede Norte» (código `PRINCIPAL`), una sola. | |

## F. Recuperar la contraseña
| # | Qué hacer | Debe pasar | Resultado |
|---|---|---|---|
| F1 | Cerrar sesión → «¿Olvidó su contraseña?». Gimnasio `fitness-quito-norte`, correo `nadie@correo.test` | Mensaje «Si los datos corresponden a una cuenta, enviamos un enlace…». En Mailpit **no** llega nada. | |
| F2 | Repetir con el correo real `maria.torres@correo.test` | **El mismo mensaje exacto.** En Mailpit llega «Restablezca su contraseña». | |
| F3 | Repetir F2 inmediatamente (en menos de 1 minuto) | Mismo mensaje; **no** llega un segundo correo. | |
| F4 | Abrir el enlace del correo | Pantalla «Elegir una contraseña nueva». La barra de direcciones ya no tiene `token=`. | |
| F5 | Contraseña `debil`, confirmación `otra` → Guardar | «La contraseña no cumple los requisitos» y «Las contraseñas no coinciden». | |
| F6 | Contraseña `Nueva2026Clave` en ambas → Guardar | «Listo: su contraseña se cambió y se cerraron las sesiones abiertas». En Mailpit llega «Se cambió su contraseña». | |
| F7 | Iniciar sesión con `Gimnasio2026Ok` (la vieja) | Credenciales inválidas. | |
| F8 | Iniciar sesión con `Nueva2026Clave` | Entra. | |
| F9 | Abrir otra vez el enlace de F4 | «El enlace no es válido, ya se usó o venció». | |

## G. Invitar al equipo (con el límite del plan Inicio: 3 usuarios, la propietaria ya es 1)
| # | Qué hacer | Debe pasar | Resultado |
|---|---|---|---|
| G1 | Menú **Invitaciones** → «Invitar a una persona»: correo `carlos.recepcion@correo.test`, rol **Recepcion**, marcar «Sede Norte» → Enviar | «Invitación enviada.» Aparece «Pendiente». El resumen del inicio dice «1 invitaciones pendientes». | |
| G2 | Invitar `lucia.cajera@correo.test`, rol **Cajero** | Enviada (ya hay 2 reservadas + la propietaria = 3, el tope del plan). | |
| G3 | Invitar `pedro.contador@correo.test`, rol **Contador** | Rechazada con un mensaje del límite del plan («Su plan Inicio permite 3 usuarios…»). | |
| G4 | Invitar otra vez a `carlos.recepcion@correo.test` | «Ya hay una invitación pendiente para ese correo. Reenvíela o revóquela.» | |
| G5 | Invitar el correo de la propietaria (`maria.torres@correo.test`) | «Ya existe un usuario con ese correo.» | |
| G6 | **Revocar** la invitación de Lucía | Pasa a «Revocada». Ahora invitar a `pedro.contador@correo.test` (Contador) **sí** se puede. | |
| G7 | En Mailpit abrir el enlace de Lucía (revocada) en ventana privada | «La invitación no es válida, ya se usó o venció». | |
| G8 | Abrir el enlace de Carlos en ventana privada | Muestra «Lo invitaron a unirse a «Fitness Quito Norte» con el rol Recepcion» e «Invitación para carlos.recepcion@correo.test». | |
| G9 | Nombre `Carlos Mena`, contraseña `debil` → Crear mi cuenta | «La contraseña no cumple los requisitos» (el enlace **no** se gasta). | |
| G10 | Contraseña `Invitado2026Ok` en ambas → Crear mi cuenta | «¡Listo! Su cuenta está creada.» Botón Iniciar sesión con el gimnasio ya escrito. | |
| G11 | Entrar como Carlos | Ve solo lo de recepción: **no** ve Invitaciones, Usuarios ni el Resumen de su gimnasio. | |
| G12 | Como María, mirar Invitaciones | Carlos «Aceptada», Lucía «Revocada», Pedro «Pendiente». **Reenviar** a Pedro dentro del primer minuto: aviso de espera; pasado el minuto, llega un correo nuevo y el anterior deja de servir. | |

## H. Plataforma
| # | Qué hacer | Debe pasar | Resultado |
|---|---|---|---|
| H1 | Entrar como administrador de plataforma → **Solicitudes comerciales** | Ver la solicitud de Jorge Andrade (3 veces, por B4) con tipo, correo, teléfono, gimnasio y mensaje; estado «Nueva». | |
| H2 | **Marcar atendida** en una | Cambia a «Atendida» y avisa. Con el filtro «Nueva» ya no aparece. **Reabrir** la devuelve. | |
| H3 | **Planes comerciales**: columna «Sitio público» y botón **Ocultar** en `gratis` | El plan pasa a «Oculto». Recargar `/planes` (ventana privada): ya no está Gratis. | |
| H4 | **Publicar** otra vez `gratis` | Vuelve a verse en `/planes`. | |
| H5 | Intentar **Publicar** el plan `retirado` | Rechaza: «Solo un plan activo y vendible puede mostrarse en el sitio público». | |
| H6 | Entrar como María (gimnasio) e intentar abrir `/platform/leads` | No tiene acceso (pantalla «Sin acceso»). | |

## I. Celular, accesibilidad y seguridad a ojo
| # | Qué hacer | Debe pasar | Resultado |
|---|---|---|---|
| I1 | En el navegador, herramientas de desarrollo → vista de celular **360 px**. Abrir `/`, `/planes`, `/contacto`, `/registro` | Ninguna página se desplaza hacia los lados; el botón «Menú» despliega la navegación. | |
| I2 | Con el teclado solamente (Tab/Shift+Tab/Enter), completar el formulario de contacto | Todo es alcanzable, el foco se ve claramente y los errores aparecen junto al campo. | |
| I3 | Zoom del navegador al 200 % en `/registro` | Se sigue pudiendo leer y usar sin que se corte el contenido. | |
| I4 | Modo oscuro del sistema operativo | Los textos se leen bien en la portada, Planes y Registro. | |
| I5 | Con un correo que **sí** existe y otro que **no** (F1/F2, y en el reenvío de C7) | La pantalla nunca revela si la cuenta existe. | |

## J. Casos avanzados (opcionales, requieren una orden en la terminal)
**Enlace vencido** (J1): hacer un registro nuevo (por ejemplo `prueba.vence@correo.test`), y antes de abrir el correo ejecutar:
```powershell
docker exec fitcore360-postgres-1 psql -U fitcore -d fitcore -c "UPDATE identity.account_tokens SET expires_at = now() - interval '1 minute' WHERE purpose = 'verify_email' AND used_at IS NULL;"
```
Al abrir el enlace debe decir «El enlace no es válido o ya se usó» y ofrecer pedir otro.

**Sin servidor de correo** (J2): detener Mailpit **no** basta (el Worker reintenta). Para probar el aviso, en `infra/compose/.env` agregue `SMTP_HOST=` (vacío), reinicie `api` y `worker`, e intente registrarse: debe decir «El envío de correo no está disponible por ahora», sin crear nada. Quite la línea y reinicie para volver a la normalidad.

## Cierre de la prueba
Cuéntemelo así, caso por caso:
> `A4: falló — veo también el plan «Gratis» dos veces` · `E6: ok` · …

Los casos que fallen los corrijo con prioridad. Lo que **no** se prueba aquí (porque aún no existe): cobro automático con tarjeta, facturación electrónica del SRI, textos legales definitivos y CAPTCHA.
