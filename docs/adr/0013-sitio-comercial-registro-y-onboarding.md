# ADR 0013 — Sitio comercial, registro de gimnasios y onboarding

Estado: **aceptado el 2026-10-30** (bloque 1). Corresponde a la «FASE 7» del prompt del usuario (nuestra numeración: **Fase 8**). Amplía las fases 3 (alta de gimnasios, usuarios, roles) y 7 ([ADR 0012](0012-modulo-subscriptions.md): planes y suscripciones). No reconstruye autenticación, tenants, permisos ni suscripciones: los reutiliza.

## Decisiones confirmadas (2026-10-30)
1. **Sitio público dentro de la misma app Angular, con páginas públicas prerenderizadas** (HTML estático para inicio, funcionalidades, planes, nosotros, preguntas frecuentes y contacto), de modo que Google y las vistas previas de WhatsApp/redes vean título, descripción e imagen. Los planes se cargan en vivo desde la API. *Plan B declarado:* si el prerenderizado de Angular diera problemas, se publica sin él (metaetiquetas por ruta) y se informa; el resto no cambia.
2. **«/» es el sitio público para visitantes; quien inició sesión ve su panel en la misma dirección.** No cambia ninguna dirección interna ni prueba del panel. Las páginas públicas llevan nombres en español (`/funcionalidades`, `/planes`, `/nosotros`, `/preguntas-frecuentes`, `/contacto`, `/registro`).
3. **Correo por SMTP genérico** (host, puerto, usuario, clave, remitente), sin atarse a un proveedor. En desarrollo hay un buzón local (**Mailpit**) donde se ven los correos reales enviados. **Sin SMTP configurado el sistema no simula nada**: el registro público responde que el correo no está disponible y no crea nada.
4. **Textos legales:** el sistema guarda **qué versión** de los Términos y de la Política de privacidad aceptó cada persona y cuándo; las páginas muestran el texto desde archivos versionados. Se incluye un **borrador base marcado «pendiente de revisión legal»**; el usuario o su abogado lo reemplazan antes de abrir al público. No es asesoría jurídica.

## Diagnóstico del repositorio (qué hay y qué falta)
| Pieza | Estado hoy | Qué se hace |
|---|---|---|
| Alta de gimnasio | Existe, solo para la plataforma: `TenantProvisioningService` (gimnasio + roles + propietario, transaccional) y el punto de extensión `ITenantProvisioningHook` (plan y prueba, Fase 7). | **Se reutiliza.** Se agrega una sobrecarga que acepta una contraseña ya hasheada (el registro público la guarda hasheada hasta verificar el correo). |
| Planes y suscripciones | Completo (ADR 0012): versiones, prueba, límites, `ICapabilityEvaluator`, panel y portal. No hay noción de «plan público». | Se agrega la marca explícita **`is_public`** al plan (el equipo decide cuáles se ofrecen; ningún plan activo se publica solo). |
| Autenticación | Login, refresh rotativo, logout, cambio de clave, `MustChangePassword`. **No hay** verificación de correo, recuperación de clave ni invitaciones. Los usuarios se crean con `EmailConfirmed = true`. | Se **amplía** (no se reemplaza): tokens propios de un solo uso, endpoints de verificación, recuperación e invitación. |
| Correo | **No existe** ningún emisor (ni puerto ni implementación). | Puerto `IEmailSender` en BuildingBlocks + SMTP real + cola con reintentos. |
| Archivos privados | `IFileStore` y logo por gimnasio (Fase 8 previa). | Se reutiliza para el logo del onboarding. |
| Tenants | Estado `Active/Suspended`, nombre, zona, moneda, idioma. Sin correo ni teléfono de contacto ni país. | Columnas de contacto y `setup_status` (ver abajo). |
| Sucursal | Código, nombre, zona, código SRI, dirección. Sin teléfono ni horarios. | Se agregan teléfono y horario semanal. |
| Configuración operativa | Política de acceso con deuda y porcentaje mínimo (Commercial). | Se reutiliza; el onboarding la edita a través de un contrato de BuildingBlocks (Platform no referencia Commercial). |
| Angular | Solo rutas privadas y `/login`; una sola SPA sin SSR (Angular 22). Estilos propios (FIT360), sin Material. | Páginas públicas nuevas con el mismo sistema de estilos; prerenderizado de las estáticas. |
| Límites de frecuencia | Políticas por IP para login y sesión. | Se agregan políticas para registro, contacto, verificación, recuperación e invitaciones, y contadores por correo. |

## Diseño

### Estados (no se mezclan)
- **Registro (antes de existir el gimnasio):** `Pending` → `Verified` (se creó el gimnasio) | `Expired` | `Rejected`. La «pendiente de verificación» es un estado del **registro**, no del gimnasio: así un correo no verificado nunca deja un gimnasio, un usuario ni un nombre ocupados para siempre. El identificador (slug) queda reservado mientras el registro esté vigente (24 h).
- **Gimnasio:** `status` (`Active`/`Suspended`, administrativo, ya existe) y **nuevo `setup_status`** (`PendingSetup` → `Ready`), independiente. Los gimnasios existentes pasan a `Ready`. «Pendiente de configuración» = gimnasio creado cuyo onboarding no terminó; **no bloquea el uso** de lo ya habilitado, solo muestra pendientes.
- **Suscripción:** la de Fase 7 (Trialing, Active, PastDue…). Registrarse **nunca activa un plan de pago**.

### Registro público
1. `POST /public/registrations` valida (formato, política de clave, slug libre, plan público vigente, términos) y guarda un **registro pendiente** con la clave **hasheada** (nunca en claro) y un token de verificación (se guarda solo su SHA-256). Respuesta **siempre igual** (202, «revise su correo») exista o no la cuenta: no se puede enumerar correos.
2. El correo llega por la cola de correo. Sin SMTP: `503 registration.email_unavailable` y no se guarda nada.
3. `POST /public/registrations/verify-email` (token de un solo uso, expira en 24 h): en **una transacción** crea el gimnasio con `TenantProvisioningService`, el propietario (rol inicial de propietario, jamás un rol de plataforma), la sucursal principal, la suscripción inicial (hook de Fase 7), los registros de aceptación de términos y la auditoría; marca el registro como `Verified`. Reintentar el mismo token no duplica nada.
4. `POST /public/registrations/resend-verification`: reenvío controlado (límite por correo e IP; respuesta genérica).

### Plan y prueba gratuita
- Solo se ofrecen planes **públicos** (`is_public`), activos y no heredados. El precio, la moneda, la periodicidad y la versión salen **siempre del servidor**; el cliente solo envía el código del plan y se rechaza cualquier importe enviado.
- **Autoservicio solo para planes con prueba gratuita o de precio 0.** Un plan de pago sin prueba no se contrata por registro (no hay proveedor de pagos): la pantalla ofrece «hablar con ventas» (solicitud de contacto). Así ninguna elección activa un cobro.
- **Una prueba por propietario:** se registra la reclamación de la prueba con el **correo normalizado** (minúsculas, sin `+etiqueta`). Si ya la usó, **al verificar** (la persona ya demostró ser dueña del correo, así que no es enumeración) se le informa y se le ofrece contactar ventas; no se crea el gimnasio. No impide que alguien con otro correo pida otra prueba: se mitiga con límites de frecuencia y revisión de la plataforma; no se promete más.

### Recuperación de clave y tokens
Tabla de **tokens de cuenta** propia (propósito, SHA-256 del token, vencimiento, un solo uso, revocable): recuperación de clave y reenvíos. No se usa el proveedor de tokens de Identity porque depende de llaves de protección de datos que se perderían al reconstruir contenedores. Restablecer revoca las sesiones (refresh tokens) de la cuenta. Respuestas genéricas, límites por correo e IP, sin tokens ni claves en registros.

### Onboarding persistente
Estado en el backend (`tenant_onboarding`: paso actual, fecha de cada paso, pasos omitidos, finalización), no en el navegador. Pasos: **perfil** (contacto, país, zona, moneda, logo opcional), **sucursal principal** (dirección, teléfono, horarios), **configuración operativa** (política de acceso con deuda), **equipo** (invitaciones, opcional) y **finalización** con resumen. Cada paso es idempotente (repetirlo actualiza, no duplica) y se puede retomar tras cerrar sesión. Se pueden omitir los opcionales; los datos necesarios para usar cada módulo se exigen donde corresponde.

### Invitaciones
Tabla por gimnasio (RLS): correo normalizado, rol, sucursales, vencimiento (7 días), SHA-256 del token, creada por, estado (`Pending`/`Accepted`/`Revoked`/`Expired`). El token **no sirve para otro gimnasio** (el destino sale de la fila, no del cliente). Un propietario solo puede asignar roles de su gimnasio y no uno con permisos que él no tenga ni de plataforma; se respeta el límite de usuarios del plan al invitar y al aceptar. Sin SMTP no se envían invitaciones.

### Contacto y demostraciones
Formularios públicos con validación en el servidor, límite por IP y por correo, sin guardar más que nombre, correo, teléfono opcional, nombre del gimnasio opcional y mensaje (≤ 1000). Consentimiento explícito. La plataforma los ve y marca «atendida» en una pantalla nueva; se purgan a los 180 días. Si hay SMTP y un destinatario configurado, se notifica al equipo comercial; si no, solo quedan en la pantalla.

### Seguridad y privacidad (transversal)
Aislamiento por gimnasio en todo lo nuevo (RLS donde hay `tenant_id`); ningún endpoint público devuelve datos privados; tokens aleatorios de 32 bytes, solo su hash en la base, expiración y un solo uso; respuestas genéricas; límites por IP **y** por correo; validación y normalización en el servidor; sin contraseñas ni tokens en registros ni auditoría; archivos solo PNG/JPEG validados en el almacén privado; aceptación legal con versión y fecha; mínimo de datos personales; CORS y cookies como hoy (mismo origen). No hay CAPTCHA obligatorio (sin integración definida); queda un punto de extensión para añadirlo.

### Sitio público, SEO y rendimiento
Páginas con título, descripción, Open Graph y canonical propios; `robots.txt` y `sitemap.xml`; rutas privadas con `noindex`; imágenes optimizadas; navegación accesible por teclado; 404 con página propia. Se muestran **solo funciones realmente implementadas** (las demás, marcadas como «próximamente») y **sin precios ni testimonios inventados**: los planes vienen del catálogo.

## Bloques de implementación
| # | Bloque | Estado |
|---|---|---|
| 1 | Diagnóstico y ADR 0013 | Hecho |
| 2 | Correo: puerto `IEmailSender`, SMTP real, cola con reintentos, plantillas es/en, Mailpit, tokens de cuenta | Hecho |
| 3 | Planes públicos (`is_public`, endpoints, límites de frecuencia, pantalla de plataforma) | Pendiente |
| 4 | Registro del propietario y creación transaccional del gimnasio (con verificación y prueba única) | Pendiente |
| 5 | Recuperación de contraseña y reenvíos | Pendiente |
| 6 | Onboarding persistente (backend y asistente Angular) | Pendiente |
| 7 | Invitaciones de empleados y límites de usuarios | Pendiente |
| 8 | Sitio público Angular (inicio, funcionalidades, planes, nosotros, preguntas, contacto), SEO y prerenderizado; contacto y demostraciones | Pendiente |
| 9 | Pantallas de registro, verificación, recuperación e invitación; panel del propietario | Pendiente |
| 10 | Pruebas (las 20 obligatorias), revisión de seguridad, accesibilidad y rendimiento, documentación e informe | Pendiente |

## Fuera de esta fase (anotado, no olvidado)
Facturación electrónica del SRI, nómina, app móvil (Flutter), avisos por WhatsApp/SMS, cobro automático con proveedor de pagos, inventario y punto de venta, CAPTCHA, verificación de correo de usuarios creados por el personal. Ver [hoja de ruta](../implementation-roadmap.md).
