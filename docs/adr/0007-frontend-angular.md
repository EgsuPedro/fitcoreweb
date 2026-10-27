# ADR 0007: Frontend Angular (shell, sesión e internacionalización)

**Estado:** Implementado (Fase 2B)

## Decisiones
- **Angular 22**, componentes standalone, señales, detección de cambios `OnPush`, formularios reactivos y carga perezosa por ruta. Sin librería de UI: tokens CSS y clases propias, modo oscuro automático, diseño responsivo y diálogos con el elemento nativo `<dialog>` (foco, Esc y fondo inerte los gestiona el navegador).
- **Tipos de la API generados desde su OpenAPI** (`npm run generate:api`, con la API en Development). Los endpoints declaran `.Produces<T>()` para que las respuestas formen parte del contrato. `openapi-typescript` se ejecuta con `npx` porque exige TypeScript 5 y el proyecto usa 6.
- **Sesión:** el access token vive solo en memoria. El refresh token viaja en una cookie `HttpOnly; SameSite=Strict; Path=/api/v1/auth` que el JavaScript no puede leer. Al recargar, `SessionService.restore()` pide un access token nuevo con esa cookie.
- **Interceptor:** adjunta el token, y ante un 401 hace **un** refresh (compartido entre peticiones simultáneas) y repite la petición. Si el refresh falla, o el 401 persiste (usuario desactivado, gimnasio suspendido), cierra la sesión y vuelve al login con un aviso.
- **Mismo origen para web y API:** en desarrollo, `ng serve` con proxy; en despliegue, nginx reenvía `/api`. Así la cookie no necesita CORS. La API confía en `X-Forwarded-For` solo desde las redes de `Network:TrustedProxies`.
- **Autorización en el cliente = experiencia de usuario, no seguridad.** El menú y las rutas (`permissionGuard`) se filtran por permisos para no ofrecer lo que la API rechazaría (incluida la regla anti-escalada al asignar roles), pero la API valida todo en cada solicitud.
- **Idiomas:** español por defecto e inglés (Transloco). Los errores de la API se traducen por su `code` estable (`errors.<código>`); una prueba exige que es y en tengan las mismas claves y que todos los códigos de la API estén traducidos. `<html lang>` sigue al idioma activo.
- **Cabeceras de seguridad** en nginx (CSP, `X-Frame-Options`, `nosniff`, etc.). La CSP permite estilos en línea porque Angular los emite; endurecerla con nonces queda pendiente. La CSP **no** permite scripts en línea, por eso la build de producción desactiva `inlineCritical`: esa optimización de Angular carga la hoja de estilos principal con un `<script>` en línea que la CSP bloquea, y la página quedaba sin estilos. Una prueba e2e verifica que la hoja de estilos se aplica y que no hay violaciones de CSP.

## Pruebas
- Unitarias (Vitest): sesión, interceptor, guards, menú por permisos, reglas de roles y traducciones.
- End-to-end (Playwright, navegador real contra la API y PostgreSQL reales): login y mensajes de error, redirección con `returnUrl` sin redirección abierta, alta de gimnasio → sucursal → rol → usuario, restricciones por rol, persistencia de sesión al recargar, cierre de sesión al desactivar un usuario y cambio de idioma. Se ejecutan contra `ng serve` y contra la build de producción servida por nginx.

## Pendiente
- Pantalla para editar los datos del gimnasio (`/organization`) y cambiar la propia contraseña.
- Regenerar los tipos de la API en CI y fallar si cambian sin commit.
- Revisión de accesibilidad con herramienta automatizada (axe).
