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
- **Pendiente de decidir:** el nombre visible del producto. El logo dice «FIT360» y el sistema todavía dice «FitCore 360» (título de la página, inicio de sesión y correos).

## Logo del gimnasio y banner (2026-10-29)
- **Naranja suave:** el color de la acción principal es un durazno (`--cta: #fdba74`, borde `#fb923c`, texto marrón oscuro `#431407`); más intenso al pasar el ratón y al hacer clic. Ya no es el naranja saturado.
- **Banner de Fiti** en la columna derecha del inicio (en pantallas angostas pasa debajo).
- **Logo del gimnasio:** se carga al dar de alta el gimnasio (plataforma) y se cambia en «Logo del gimnasio» (propietario) o desde la ficha de la organización (plataforma). PNG o JPG, hasta 1 MB, de 32 a 2000 px por lado; el servidor le quita los metadatos (ubicación, equipo). Aparece en el menú superior y en los libros de Excel; los comprobantes futuros lo usarán por el mismo contrato (`ITenantBranding`).
