import { RenderMode, ServerRoute } from '@angular/ssr';

/**
 * Qué se genera como HTML al compilar. Solo las páginas públicas del sitio comercial se prerenderizan (así Google y las vistas previas de WhatsApp o redes ven su título, descripción e
 * imagen sin ejecutar nada); el panel y el resto son de navegador. Los planes se cargan en el navegador (el prerenderizado no tiene servidor al que preguntar).
 */
export const serverRoutes: ServerRoute[] = [
  { path: '', renderMode: RenderMode.Prerender },
  { path: 'funcionalidades', renderMode: RenderMode.Prerender },
  { path: 'planes', renderMode: RenderMode.Prerender },
  { path: 'nosotros', renderMode: RenderMode.Prerender },
  { path: 'preguntas-frecuentes', renderMode: RenderMode.Prerender },
  { path: 'contacto', renderMode: RenderMode.Prerender },
  { path: 'registro', renderMode: RenderMode.Prerender },
  { path: 'terminos', renderMode: RenderMode.Prerender },
  { path: 'privacidad', renderMode: RenderMode.Prerender },
  { path: '**', renderMode: RenderMode.Client },
];
