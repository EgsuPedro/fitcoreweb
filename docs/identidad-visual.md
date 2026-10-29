# Identidad visual FIT360

Estilos en `src/web/src/styles.scss` (variables CSS propias; el proyecto **no usa Angular Material**). Logo y mascota en `src/web/public/brand/` (PNG con fondo transparente).

| Token | Valor | Uso |
|---|---|---|
| `--accent` | `#0f766e` (teal) | Menú lateral, títulos, enlaces, foco, estados activos |
| `--accent-strong` | `#0b5e57` | Títulos y texto sobre fondos claros del teal |
| `--cta` | `#f97316` (naranja) | **Acción principal** (`.btn-primary`), llamadas a la acción |
| `--cta-hover` / `--cta-active` | `#ea6a0a` / `#c2570a` | Pasar el ratón / clic (el texto pasa a blanco al hacer clic) |
| `--bg` / `--surface` | `#f8fafc` / `#ffffff` | Fondo general / tarjetas |

- **Botones:** reposo, hover, clic (`:active`) y foco siempre visibles. Cada variante define su propio hover con mayor especificidad que el botón base, para que el texto nunca quede sobre un fondo del mismo color. Texto oscuro sobre naranja (contraste AA); blanco al hacer clic.
- **Tipografía:** Inter, autoalojada con `@fontsource/inter` (sin CDN; la política de seguridad solo permite recursos propios).
- **Menú lateral:** degradado teal; el enlace activo lleva un borde naranja. El logo va sobre una tarjeta blanca.
- **Mascota «Fiti»:** componente `fc-mascot-card` (`shared/mascot-card.ts`), usado en el inicio del personal. Hoy es una imagen estática; el espacio queda listo para una animación Lottie/GIF.
- **Modo oscuro:** hay una variante de todos los tokens.
- **Nombre del producto:** FIT360 (decidido el 2026-10-29); reemplaza a «FitCore 360» en todo texto visible. Los identificadores técnicos (espacios de nombres, esquemas, contenedores) conservan el prefijo `FitCore`.

## Logo del gimnasio y banner (2026-10-29)
- **Naranja suave:** el color de la acción principal es un durazno (`--cta: #fdba74`, borde `#fb923c`, texto marrón oscuro `#431407`); más intenso al pasar el ratón y al hacer clic. Ya no es el naranja saturado.
- **Banner de Fiti** en la columna derecha del inicio (en pantallas angostas pasa debajo).
- **Logo del gimnasio:** se carga al dar de alta el gimnasio (plataforma) y se cambia en «Logo del gimnasio» (propietario) o desde la ficha de la organización (plataforma). PNG o JPG, hasta 1 MB, de 32 a 2000 px por lado; el servidor le quita los metadatos (ubicación, equipo). Aparece en el menú superior y en los libros de Excel; los comprobantes futuros lo usarán por el mismo contrato (`ITenantBranding`).

## Celular (2026-10-29)
- **Barra superior** (≤ 52 rem): solo menú, marca (logo del gimnasio o FIT360), avisos e idioma. **Mi contraseña** y **Cerrar sesión** pasan al pie del menú lateral. En escritorio siguen en la barra.
- **Sin desplazamiento horizontal:** las tablas anchas se desplazan dentro de su espacio; los títulos largos se parten; el banner de Fiti pasa a una columna. Una prueba automática (`tests/e2e/tests/mobile.spec.ts`, 360 y 440 px) recorre las pantallas principales y falla si algo se sale del ancho.
- **Datos móviles:** en pantallas angostas (y con «reducir movimiento») el banner muestra la imagen fija y **no descarga** el GIF animado (7.1 MB).
