# Hoja de ruta

## Hecho
| Fase | Contenido | Estado |
|---|---|---|
| 0 | Arquitectura, decisiones y ADR | Aprobada |
| 1 / 2A | Núcleo: multiempresa (4 capas), identidad, JWT con refresh rotativo, roles y permisos con edición, auditoría, outbox, Docker, CI | Hecha |
| 2B | Angular: login, menú por permisos, sucursales, usuarios, roles, auditoría, gimnasios; pruebas e2e | Hecha |

Pruebas actuales: 88 de backend (13 unitarias, 9 de arquitectura, 66 de integración con PostgreSQL real), 35 unitarias de Angular y 13 e2e con Playwright. CI en GitHub Actions: backend, web, e2e y docker.

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
| 3 | Alumnos, planes y membresías, obligaciones de pago, pagos por transferencia con comprobante manual, caja. IVA confirmado en **15 %** por defecto, como tabla de tarifas con vigencia editable por plataforma para cambios por decreto (ADR 0003) |
| 4 en adelante | Asistencia y control de acceso, entrenamiento, nutrición, inventario y punto de venta, reportes, facturación electrónica SRI (Fase 8, firma XML directa tras un puerto), pasarela de tarjeta (puerto, en espera), app móvil Flutter |

El orden de las fases 4 en adelante se reconfirma con el usuario al terminar cada fase.
