# Guía de pruebas por pantalla — Fase 5 (clases grupales, reservas y avisos)

Para validar a mano, pantalla por pantalla, lo que entrega la Fase 5. Cada prueba dice **qué hacer** y **qué debe pasar**. Si algo no coincide, anótelo con el número de la prueba (por ejemplo «P-7.3»).

## 0. Antes de empezar

**Entorno** (el mismo de siempre, ya levantado con Docker):
- Web: http://localhost:8081 · API: http://localhost:8080 (Swagger en `/swagger`).
- Deben estar corriendo `api`, `worker`, `web` y `postgres`: `docker compose -f infra/compose/docker-compose.dev.yml ps`. **Sin el `worker` no se generan avisos ni vencen las invitaciones** (pruebas P-6 y P-8).
- Gimnasio de revisión: **`revision`**, clave **`DemoFit2026x`** para todos (ya tiene los permisos nuevos cargados). Si crea otro gimnasio, ejecute antes `scripts/demo-seed.ps1` (ver la guía de ejecución).

**Usuarios** (gimnasio `revision`):

| Correo | Rol | Para qué se usa aquí |
|---|---|---|
| propietario@revision.fitcore.test | Propietario | Crear clases, horario, series, todo |
| admin.uio@revision.fitcore.test | AdministradorSucursal | Igual, solo Quito Norte |
| recepcion.uio@revision.fitcore.test | Recepcion | Reservar por el alumno, asistencia (no crea clases) |
| entrenador.uio@revision.fitcore.test | Entrenador | Instructor: ve su trabajo y registra asistencia de **sus** sesiones |
| cajero.gye@revision.fitcore.test | Cajero | No debe ver nada de clases |
| alumno.uio@revision.fitcore.test | Alumno | Solo si está vinculado a una ficha (ver P-9.0) |

**Tiempo.** Todas las horas se muestran y se escriben en la **hora de la sucursal** (Ecuador, UTC-5), no en la del navegador. Cree las sesiones de **mañana en adelante** (reservar exige anticipación mínima y no se programa en el pasado).

**Datos de apoyo que necesitará** (créelos con el propietario si no existen):
1. Dos sucursales (Quito Norte y Guayaquil Centro) — vienen del seed.
2. Al menos un **entrenador con perfil** (el de entrenador.uio, si el seed lo creó; si no, en *Entrenadores* → «Nuevo» sobre ese usuario).
3. **Tres alumnos** de Quito Norte, para las pruebas de cupo: «Ana Prueba», «Beto Prueba» y «Carla Prueba» (en *Alumnos*). Los alumnos **no necesitan membresía** si la clase no la exige (se desmarca al crear la clase). Para probar elegibilidad (P-4.6) uno de ellos sí necesita membresía.
4. Para el portal (P-9): conceda acceso a Ana y a Beto desde su ficha (*Acceso al portal* → «Dar acceso»); la pantalla muestra una **clave temporal una sola vez**; al entrar por primera vez el alumno debe cambiarla.

---

## P-1. Menú y permisos
| # | Haga | Debe pasar |
|---|---|---|
| 1.1 | Entre como **propietario**. | El menú lateral tiene **Horario**, **Clases** e **Historial de clases**. La barra superior tiene una **campana** de avisos. |
| 1.2 | Entre como **cajero.gye**. | **No** aparecen Horario, Clases ni Historial de clases. Escribir `/classes` en la barra del navegador lo manda a «Sin acceso». La campana existe para todos los usuarios (solo muestra avisos propios). |
| 1.3 | Entre como **recepción**. | Ve Horario y Clases (solo lectura: **no** hay botón «Nueva clase») e Historial de clases. |
| 1.4 | Entre como **entrenador**. | Ve Horario, Clases (solo lectura) e Historial de clases. |
| 1.5 | Cambie el idioma a inglés y vuelva a español. | Las pantallas de clases y la campana cambian de idioma sin textos sin traducir. |

## P-2. Clases (`/classes`)
| # | Haga | Debe pasar |
|---|---|---|
| 2.1 | Propietario → **Clases** → «Nueva clase». Deje todo vacío y guarde. | No guarda: marca los campos obligatorios (sucursal, nombre, instructor…). |
| 2.2 | Cree «**Yoga mañana**»: sucursal Quito Norte, actividad Yoga, instructor el entrenador, duración 60, **capacidad 2**, nivel «Todos», lista de espera **activada** con **1** persona, plazo para aceptar **30** min. **Desmarque** «Exigir membresía activa» (primero la política de pagos, luego la membresía). | Aviso «Clase creada.» y aparece en la tabla como Activa. |
| 2.3 | Cree otra clase con el **mismo nombre** en la misma sucursal. | Error claro de nombre repetido. En otra sucursal el mismo nombre sí se permite. |
| 2.4 | Intente marcar «Aplicar política de pagos» sin «Exigir membresía». | Lo impide con un mensaje (la política de pagos solo aplica con membresía). |
| 2.5 | Edite «Yoga mañana»: cambie la descripción. Intente cambiar la **sucursal**. | La descripción se guarda; la sucursal está bloqueada (explica que para otra sucursal se crea otra clase). |
| 2.6 | Desactive la clase y vuelva a activarla. | El estado cambia en la tabla. Mientras está inactiva, **no** se le puede programar sesiones (P-3.7). |
| 2.7 | Entre como **recepción** o **entrenador** y abra Clases. | Ve la lista pero no puede crear ni editar. |
| 2.8 | Como **admin.uio**, mire el filtro de sucursal. | Solo ve Quito Norte; las clases de Guayaquil no aparecen. |

## P-3. Horario y sesiones (`/schedule`)
Cree también una segunda clase, «**Spinning tarde**» (capacidad 10, sin lista de espera), para tener variedad.

| # | Haga | Debe pasar |
|---|---|---|
| 3.1 | Propietario → **Horario**. Cambie entre **Día / Semana / Mes**, use Anterior / Siguiente / Hoy. | El calendario se mueve; «Hoy» está resaltado; el título indica el periodo. Muestra «Hora de la sucursal (America/Guayaquil)». |
| 3.2 | «Programar sesión»: clase «Yoga mañana», fecha de **mañana**, hora **09:00**. Guarde. | «Sesión programada.» y aparece en el calendario a las 09:00, con 0 / 2 ocupados. |
| 3.3 | Programe «Spinning tarde» mañana 09:30 **con el mismo instructor**. | **Error de superposición del instructor**: no deja (mismo instructor, horas que se cruzan). |
| 3.4 | Repita en 3.3 con otra hora que no se cruce (11:00). | Se programa. |
| 3.5 | Programe una sesión a una hora **fuera de la disponibilidad** del instructor (si le definió franjas en *Entrenadores*). | Pide un **motivo de excepción**; con motivo se guarda y la sesión muestra la marca de excepción. Si el instructor no tiene franjas, no se restringe. |
| 3.6 | Intente programar una sesión **en el pasado**. | Error claro: no se programa en el pasado. |
| 3.7 | Desactive «Spinning tarde» (P-2.6) y trate de programarle una sesión. | Error: la clase está inactiva. |
| 3.8 | Abra una sesión (clic) → «Editar»: cambie la hora un par de horas. | Se guarda y la sesión queda marcada como **modificada**. Cambiar la capacidad a 1 con 2 reservas confirmadas (P-4) la rechaza (no baja de lo ocupado). |
| 3.9 | «**Serie recurrente**»: «Yoga mañana», días **lunes y miércoles**, 09:00, desde mañana hasta dentro de 4 semanas. | Informa cuántas sesiones creó (≈ 8) y cuáles omitió (por ejemplo, las que se cruzaban). Aparecen en el calendario. |
| 3.10 | Repita **exactamente** la misma serie / use «Extender» sobre la misma serie. | **No duplica**: informa 0 nuevas (la generación es idempotente). |
| 3.11 | Cancele **una** sesión de la serie (con motivo). Luego extienda la serie. | La cancelada **no resucita**. |
| 3.12 | «Cancelar serie» con motivo. | Se cancelan las sesiones futuras de la serie; las ya ocurridas no. |

## P-4. Reservas (panel de la sesión, pestaña «Reservar»)
Use la sesión de «Yoga mañana» de capacidad 2.

| # | Haga | Debe pasar |
|---|---|---|
| 4.1 | Clic en la sesión → pestaña **Reservar** → busque «Ana» → Reservar. | «Reserva registrada.»; Ana aparece en **Participantes** como Confirmada; «1 de 2 ocupados». |
| 4.2 | Reserve a Ana **otra vez**. | Error: ya tiene reserva en esta sesión (no se duplica). |
| 4.3 | Reserve a Beto. Luego a Carla **sin** marcar lista de espera. | Beto entra (2 de 2). Carla: «**La sesión está llena.**» y no queda en ninguna fila. |
| 4.4 | Reserve a Carla marcando «Si la sesión está llena, anotar en la lista de espera». | Entra a **Lista de espera** (puesto 1); el resumen dice «1 en espera». No ocupa cupo. |
| 4.5 | Con **otro** alumno intente anotarse en espera cuando la fila ya tiene 1 (tope 1). | Error: la lista de espera está llena. |
| 4.6 | (Elegibilidad) Cree una clase que **sí exija membresía activa** y reserve a un alumno **sin membresía**; luego con una membresía activa pero con deuda vencida (si la política de pagos aplica). | Rechaza con el **motivo específico** (sin membresía activa / deuda vencida…). Con membresía activa y al día, reserva. |
| 4.7 | (Conflicto) Reserve al mismo alumno en otra sesión que se **cruce en el horario** con la primera. | Error: ya tiene otra clase a esa hora. |
| 4.8 | (Plazos) Intente reservar una sesión que empieza en menos del cierre de reservas (p. ej. cierre de 30 min y sesión en 10 min). | Error: reservas cerradas. |
| 4.9 | Doble clic rápido en «Reservar». | Una sola reserva (idempotencia). |

## P-5. Cancelar reservas y lista de espera
| # | Haga | Debe pasar |
|---|---|---|
| 5.1 | En Participantes, cancele la reserva de **Ana** («Cancelar reserva», sin motivo si está dentro del plazo). | «Reserva cancelada.» El cupo se **reserva para Carla** (la primera en espera): en la pestaña **Cupos ofrecidos** Carla aparece como «Cupo ofrecido» con «Responde hasta las HH:MM». La ocupación sigue 2 / 2 (el cupo ofrecido cuenta). |
| 5.2 | Con otro alumno intente reservar mientras el cupo está ofrecido. | «Llena»: nadie puede colarse en el cupo reservado. |
| 5.3 | Pulse **Aceptar** en la oferta de Carla. | «Cupo confirmado.»; Carla pasa a Participantes como Confirmada. |
| 5.4 | Repita 5.1 con otra cancelación y pulse **Rechazar** la oferta. | La oferta pasa al siguiente en espera; si no hay, el cupo queda libre. |
| 5.5 | (Fuera de plazo) Cancele la reserva de una sesión que empieza dentro del plazo de cancelación de la clase (p. ej. plazo 120 min y sesión en 1 h). | Pide **motivo obligatorio**; sin motivo no deja; con motivo cancela y queda marcada como «fuera de plazo». |
| 5.6 | Quite a alguien de la lista de espera con «Sacar de la lista». | Desaparece de la fila; los puestos se recorren. |

## P-6. Vencimiento de ofertas (requiere el `worker`)
| # | Haga | Debe pasar |
|---|---|---|
| 6.1 | Cree una clase con plazo para aceptar **el mínimo permitido**, llene una sesión, ponga a alguien en espera y cancele una reserva. **No acepte.** Espere el plazo + ~30 s. | La oferta **vence**: el cupo pasa al siguiente elegible o queda libre. El alumno vencido deja de tener oferta. |
| 6.2 | Mire la campana del alumno que ofreció/venció. | Recibió el aviso de que se le ofreció un cupo (y, si corresponde, el de vencimiento). |

## P-7. Asistencia a clase
Para probar la asistencia la sesión debe **haber empezado**. Cree una sesión que empiece **en unos minutos** (la hora mínima de reserva lo permite si la clase tiene «Cierre de reservas = 0») o use una de hoy más temprano que ya pasó con una clase de cierre 0.

| # | Haga | Debe pasar |
|---|---|---|
| 7.1 | Abra una sesión **futura** (mañana). | **No** aparecen los botones «Asistió/No asistió». |
| 7.2 | Abra una sesión que **ya empezó** con 2 confirmados. Pulse **Asistió** en uno y **No asistió** en otro. | Cada fila muestra su resultado; «Asistencia guardada.» |
| 7.3 | Cambie el resultado de uno (corrección). | Se actualiza y queda marcado como corregido. |
| 7.4 | «Marcar pendientes como «Asistió»» en una sesión con pendientes. | Los pendientes quedan en Asistió. |
| 7.5 | «Finalizar sesión» (con la opción de marcar pendientes como no asistió). | La sesión pasa a **Completada**, se cierra la lista de espera, no se reabre. |
| 7.6 | **Historial de clases**: filtre por alumno, resultado y fechas. | Lista lo registrado con clase, hora, instructor, resultado y quién lo registró; el paginador funciona. |
| 7.7 | Como **entrenador (instructor)**: abra una sesión **suya** y registre asistencia. | Funciona. |
| 7.8 | Como **entrenador**, intente una sesión de **otro** instructor. | No le deja (no la ve / 404). |
| 7.9 | La asistencia a clase **no** cambia el control de acceso del gimnasio. | En *Asistencia* (entradas) no aparece ningún registro nuevo por haber marcado la clase. |

## P-8. Cancelar sesión y avisos
| # | Haga | Debe pasar |
|---|---|---|
| 8.1 | Abra una sesión con reservas y cancélela con motivo («Mantenimiento»). | «Sesión cancelada.»; las reservas quedan canceladas y el cupo ofrecido se libera. |
| 8.2 | En ≈ 10 s entre como el **instructor** (entrenador) → campana / **Avisos** (`/inbox`). | Hay un aviso «Sesión cancelada» con el motivo; la campana muestra el contador de sin leer. |
| 8.3 | Entre como un **alumno con acceso** que tenía reserva. | Tiene el aviso de la sesión cancelada. |
| 8.4 | Abra un aviso / «Marcar todo como leído». | El contador baja a 0. |
| 8.5 | **Preferencias** (en Avisos): apague «Recordatorio de clase». Intente apagar «Clase cancelada». | El recordatorio se puede apagar; **los importantes (clase cancelada, cambio de horario, cupo disponible) están bloqueados y no se pueden apagar**. |
| 8.6 | Cambie la hora de una sesión con reservas. | Los inscritos reciben aviso de cambio. |
| 8.7 | Recargue la página varias veces. | Los avisos **no se duplican**. |
| 8.8 | Nota de alcance: | Solo existe la **bandeja interna**. No se envían correos, SMS ni WhatsApp (no hay proveedor): es correcto que no lleguen. |

## P-9. Portal del alumno (`/`, sección «Mis clases»)
| # | Haga | Debe pasar |
|---|---|---|
| 9.0 | Con el propietario, abra la ficha de Ana → *Acceso al portal* → «Dar acceso». Copie la clave temporal. Cierre sesión y entre como Ana (gimnasio `revision`, su correo, la clave temporal); cambie la clave. | Entra a su portal: «Hola, Ana», y la sección **Mis clases** con pestañas Agenda / Mis reservas / Historial. No ve menú de administración. |
| 9.1 | Pestaña **Agenda**. | Lista las sesiones **futuras de su sucursal** (no las de Guayaquil ni las canceladas), con cupos «X / Y» y las horas en la hora de su sucursal. |
| 9.2 | «Reservar» en una sesión con cupo. | «Reserva registrada.»; esa fila muestra «Confirmada» y el botón pasa a «Cancelar». |
| 9.3 | Con Beto (otra ventana de incógnito) llene la sesión de capacidad 2 y deje a Ana sin cupo; en Ana pulse **Anotarme en la espera**. | La fila decía «Llena»; tras pulsar queda «En espera», y en **Mis reservas** muestra «puesto N». |
| 9.4 | Haga que se libere un cupo (cancele a otro alumno desde el panel del personal). Recargue el portal de Ana. | En Mis reservas aparece **Cupo ofrecido** con «responda hasta las HH:MM». **Aceptar cupo** → «Cupo confirmado.»; o **Rechazar**. |
| 9.5 | **Cancelar** una reserva dentro del plazo. | Se abre «Cancelar reserva» con motivo opcional; al confirmar, «Reserva cancelada.» |
| 9.6 | Cancelar una reserva **fuera de plazo** sin motivo. | El diálogo muestra el error de motivo obligatorio; con motivo, cancela. |
| 9.7 | Después de que el personal marque asistencia, pestaña **Historial**. | Muestra «Asistió» / «No asistió» de **sus** clases solamente. |
| 9.8 | Entre como Beto y mire su portal. | No ve nada de Ana (reservas ni historial). |
| 9.9 | La campana del alumno. | Muestra los avisos de oferta, cancelación o cambio de **sus** reservas. |
| 9.10 | Como alumno, escriba `/classes` o `/schedule` en el navegador. | «Sin acceso»: son pantallas del personal. |

## P-10. Seguridad y aislamiento (pruebas rápidas)
| # | Haga | Debe pasar |
|---|---|---|
| 10.1 | Cree otro gimnasio (como plataforma) y entre con su propietario: *Clases* y *Horario*. | Vacíos: no ve nada de `revision`. |
| 10.2 | Como **admin.uio**, en Swagger pida `GET /api/v1/class-sessions` de una sesión de Guayaquil (por id). | **404** (no se revela que existe). |
| 10.3 | En Swagger, como alumno, llame `POST /api/v1/class-reservations` (la del personal). | **403**. Su propio `POST /api/v1/me/class-reservations` sí funciona y siempre reserva **a su nombre**, aunque envíe otro `memberId`. |
| 10.4 | Mire *Auditoría* (propietario). | Registra la creación de clases/sesiones, las cancelaciones y los cambios, sin datos sensibles. |

---

## Qué NO se espera (límites de esta fase)
- Envío real de correo, SMS, WhatsApp o notificaciones push (no hay proveedor; la bandeja interna es el único canal).
- Facturación electrónica del SRI, nómina, contabilidad y la app móvil Flutter (fases posteriores).
- Cierre automático de sesiones pasadas: hoy se finalizan con «Finalizar sesión».
- Limpieza automática de avisos antiguos y filtros por fecha/clase en la agenda del alumno (muestra las próximas 50 sesiones).
- Asignar un grupo de alumnos a la vez o cobrar por clase suelta.

## Si algo falla
| Síntoma | Causa probable |
|---|---|
| No llegan avisos ni vencen ofertas | El contenedor `worker` no está corriendo (`docker compose … up -d worker`) |
| «Sin acceso» para un rol que debería ver Clases | Tenant creado antes de la fase: ejecute `scripts/demo-seed.ps1 -Slug <gimnasio> -Domain <dominio> -DemoPassword <clave>` para cargar los permisos nuevos |
| La sesión no deja reservar | Revise: sesión de mañana o más (cierre de reservas), clase activa, alumno de la misma sucursal, el mensaje de la API explica el motivo |
| Horas distintas a las del reloj | Se muestran en la hora de la **sucursal**, no en la del navegador |
