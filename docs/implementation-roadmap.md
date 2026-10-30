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
| 8 | Sitio comercial, registro de gimnasios y onboarding: correo SMTP real con cola, planes públicos, registro con confirmación y prueba única, recuperación de contraseña, asistente de configuración, invitaciones, sitio público prerenderizado con SEO y panel del propietario (ver [informe](informe-fase-8.md)) | Hecha |

Pruebas actuales: ver el informe de la Fase 5 (cifras de la última regresión completa). CI en GitHub Actions: backend, web, e2e y docker.

## Pendiente de la Fase 1/2
| Tema | Nota |
|---|---|
| Recuperación de contraseña | **Hecha en la Fase 8** (correo SMTP real, enlace de un solo uso). Falta un proveedor SMTP de producción con SPF/DKIM |
| Pantallas: editar datos del gimnasio y cambiar la propia contraseña | El backend ya lo soporta |
| Tipos de la API en CI | Regenerar y fallar si hay diferencias |
| Accesibilidad | **Hecha** (axe, WCAG 2 A/AA) para el sitio público y las pantallas de cuenta en la Fase 8; falta el panel interno y una revisión manual con lector de pantalla |
| CSP con nonces | Hoy permite estilos en línea |
| Inicialización del administrador de plataforma en producción | Separar de `AutoMigrate` en un comando de despliegue propio |
| Auditoría en producción | Particionado, retención, envío a almacenamiento externo y cadena de hashes (ver `architecture.md`) |
| Caché de permisos | Hoy una consulta por solicitud; medir antes de optimizar |

## Fase en curso
| Fase | Contenido |
|---|---|
| 9 | **Facturación electrónica del SRI** ([ADR 0014](adr/0014-facturacion-electronica-sri.md)): configuración tributaria, certificado cifrado, XML y firma XAdES-BES, transmisión y autorización reales, RIDE, correo, notas de crédito. Bloque 1 (diagnóstico y plan) hecho; **no se cierra sin una autorización real en el ambiente de pruebas del SRI**. |

## Pendientes que NO se olvidan (por orden de prioridad acordado el 2026-10-29)
| Pendiente | Qué falta para empezar |
|---|---|
| **Facturación electrónica del SRI** | Certificado de firma (.p12), RUC, códigos de establecimiento y punto de emisión, acceso al ambiente de pruebas del SRI y el régimen del gimnasio (RIMPE «negocio popular» emite nota de venta, no factura). El logo del gimnasio y la numeración/IVA ya están listos. |
| **Nómina** | Definir el alcance: sueldos, comisiones por clase o alumno, IESS, décimos, roles de pago, o solo un reporte de pagos al personal. |
| **App móvil (Flutter)** | Decidir entre versión web instalable (PWA) y Flutter para tiendas; definir el alcance (portal del alumno, entrenadores). El backend ya es apto para móvil. |
| Textos legales definitivos | Reemplazar el borrador de términos y privacidad (`legal-texts.ts` + `Legal__*`) por texto revisado por un abogado antes de abrir el sitio al público. |
| Proveedor SMTP de producción | Elegir proveedor y configurar SPF/DKIM del dominio (hoy solo se probó con Mailpit y un SMTP de pruebas). |
| CAPTCHA en formularios públicos | Elegir proveedor; el campo trampa, los límites y las respuestas iguales ya están. |
| Verificar el CI de GitHub | El job e2e ahora levanta Worker, Mailpit y la web compilada; nunca se ha ejecutado allá (no hay `gh`). |
| Revisar registros `Failed` | Una pantalla en plataforma para los registros cuya suscripción no se pudo abrir (hoy solo base y registros del servidor). |
| Hosting estable de la demo | Un VPS pequeño siguiendo [despliegue.md](despliegue.md); respaldos descargados fuera del servidor y un monitor de disponibilidad. |
| Otros | GIF de Fiti más liviano (MP4/WebM), avisos por WhatsApp/SMS, cobro automático con proveedor de pagos real, inventario y punto de venta, CAPTCHA, verificar el CI de GitHub, tolerar la cookie de sesión anterior unos segundos al recargar durante una renovación. |

El orden de las fases 4 en adelante se reconfirma con el usuario al terminar cada fase.
