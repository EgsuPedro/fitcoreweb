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

Pruebas actuales: ver el informe de la Fase 5 (cifras de la última regresión completa). CI en GitHub Actions: backend, web, e2e y docker.

## Pendiente de la Fase 1/2
| Tema | Nota |
|---|---|
| Recuperación de contraseña | Aplazada por decisión del usuario; requiere proveedor de correo (puerto `IEmailSender` + flujo con token de un solo uso) |
| Pantallas: editar datos del gimnasio y cambiar la propia contraseña | El backend ya lo soporta |
| Tipos de la API en CI | Regenerar y fallar si hay diferencias |
| Accesibilidad | Revisión automatizada (axe) |
| CSP con nonces | Hoy permite estilos en línea |
| Inicialización del administrador de plataforma en producción | Separar de `AutoMigrate` en un comando de despliegue propio |
| Auditoría en producción | Particionado, retención, envío a almacenamiento externo y cadena de hashes (ver `architecture.md`) |
| Caché de permisos | Hoy una consulta por solicitud; medir antes de optimizar |

## Siguientes fases
| Fase | Contenido |
|---|---|
| 6 en adelante | Canales externos de avisos (correo/SMS/WhatsApp/push, cuando haya proveedor), inventario y punto de venta, reportes, facturación electrónica SRI (firma XML directa tras un puerto), pasarela de tarjeta (puerto, en espera), app móvil Flutter. Pendientes heredados: WhatsApp real (cuenta de Meta), recuperación de contraseña (proveedor de correo), comparación de sesiones lado a lado, asignación nutricionista–alumno |

El orden de las fases 4 en adelante se reconfirma con el usuario al terminar cada fase.
