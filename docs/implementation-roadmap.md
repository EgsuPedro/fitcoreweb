# Hoja de ruta

## Hecho
| Fase | Contenido | Estado |
|---|---|---|
| 0 | Arquitectura, decisiones y ADR | Aprobada |
| 1 / 2A | Núcleo: multiempresa (4 capas), identidad, JWT con refresh rotativo, roles y permisos con edición, auditoría, outbox, Docker, CI | Hecha |
| 2B | Angular: login, menú por permisos, sucursales, usuarios, roles, auditoría, gimnasios; pruebas e2e | Hecha |
| 3 | Comercial: alumnos, planes, membresías, cobros, caja, asistencia, avisos (ver [informe](informe-fase-3.md)) | Hecha |
| 4 | Bienestar: entrenadores, ejercicios, rutinas, nutrición, evaluaciones, objetivos, fotos privadas, Angular (ver [informe](informe-fase-4.md)) | Hecha |
| 5 | Clases grupales: sesiones y series, reservas, lista de espera con invitación, asistencia a clase, avisos internos, Angular del personal y del alumno (ver [informe](informe-fase-5.md)) | Hecha |
| 6 | Analítica: panel por rol, reportes financieros, de alumnos y de operación, comparación de sucursales, exportación a CSV (ver [informe](informe-fase-6.md)) | Hecha |
| 7 | Administración SaaS: planes comerciales, suscripciones, límites en PostgreSQL, cobros comerciales internos, panel global, portal «Mi suscripción», proveedor de pagos (solo abstracción y webhooks firmados; ver [informe](informe-fase-7.md)) | Hecha |

Pruebas actuales: ver el informe de la Fase 5 (cifras de la última regresión completa). CI en GitHub Actions: backend, web, e2e y docker.

## Pendiente de la Fase 1/2
| Tema | Nota |
|---|---|
| Recuperación de contraseña | **En curso en la Fase 8** (bloques 2 y 5): puerto `IEmailSender` + SMTP + tokens de un solo uso |
| Pantallas: editar datos del gimnasio y cambiar la propia contraseña | El backend ya lo soporta |
| Tipos de la API en CI | Regenerar y fallar si hay diferencias |
| Accesibilidad | Revisión automatizada (axe) |
| CSP con nonces | Hoy permite estilos en línea |
| Inicialización del administrador de plataforma en producción | Separar de `AutoMigrate` en un comando de despliegue propio |
| Auditoría en producción | Particionado, retención, envío a almacenamiento externo y cadena de hashes (ver `architecture.md`) |
| Caché de permisos | Hoy una consulta por solicitud; medir antes de optimizar |

## Fase en curso
| Fase | Contenido |
|---|---|
| 8 | **Sitio comercial, registro de gimnasios y onboarding** ([ADR 0013](adr/0013-sitio-comercial-registro-y-onboarding.md)): correo SMTP, planes públicos, registro con verificación, recuperación de clave, asistente de configuración, invitaciones, sitio público prerenderizado y panel del propietario. Bloque 1 (diagnóstico y diseño) hecho. |

## Pendientes que NO se olvidan (por orden de prioridad acordado el 2026-10-29)
| Pendiente | Qué falta para empezar |
|---|---|
| **Facturación electrónica del SRI** | Certificado de firma (.p12), RUC, códigos de establecimiento y punto de emisión, acceso al ambiente de pruebas del SRI y el régimen del gimnasio (RIMPE «negocio popular» emite nota de venta, no factura). El logo del gimnasio y la numeración/IVA ya están listos. |
| **Nómina** | Definir el alcance: sueldos, comisiones por clase o alumno, IESS, décimos, roles de pago, o solo un reporte de pagos al personal. |
| **App móvil (Flutter)** | Decidir entre versión web instalable (PWA) y Flutter para tiendas; definir el alcance (portal del alumno, entrenadores). El backend ya es apto para móvil. |
| Hosting estable de la demo | Un VPS pequeño siguiendo [despliegue.md](despliegue.md); respaldos descargados fuera del servidor y un monitor de disponibilidad. |
| Otros | GIF de Fiti más liviano (MP4/WebM), avisos por WhatsApp/SMS, cobro automático con proveedor de pagos real, inventario y punto de venta, CAPTCHA, verificar el CI de GitHub, tolerar la cookie de sesión anterior unos segundos al recargar durante una renovación. |

El orden de las fases 4 en adelante se reconfirma con el usuario al terminar cada fase.
