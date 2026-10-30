import { DOCUMENT, isPlatformBrowser } from '@angular/common';
import { Injectable, PLATFORM_ID, inject } from '@angular/core';
import { Meta, Title } from '@angular/platform-browser';

/** Marcador de la dirección pública del sitio: el script de publicación lo reemplaza (SITE_URL); en el navegador se resuelve con la dirección real. */
export const SITE_URL_TOKEN = '__SITE_URL__';

export interface PageSeo {
  /** Título ya traducido (sin el sufijo de la marca). */
  title: string;
  description: string;
  /** Ruta pública de la página, por ejemplo «/planes». */
  path: string;
  /** Las páginas públicas se indexan; las privadas, no. */
  index?: boolean;
}

/**
 * Título, descripción, Open Graph, Twitter, URL canónica y regla de indexación de cada página. Las páginas públicas se prerenderizan, así que Google y las vistas previas de WhatsApp o
 * redes ven estos datos sin ejecutar nada. Las páginas privadas llevan «noindex».
 */
@Injectable({ providedIn: 'root' })
export class Seo {
  private readonly title = inject(Title);
  private readonly meta = inject(Meta);
  private readonly document = inject(DOCUMENT);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  set(page: PageSeo): void {
    const index = page.index ?? true;
    const fullTitle = page.title === 'FIT360' ? 'FIT360' : `${page.title} · FIT360`;
    const base = this.browser ? this.document.location.origin : SITE_URL_TOKEN;
    const url = page.path === '/' ? `${base}/` : `${base}${page.path}`;
    const image = `${base}/brand/logo.png`;

    this.title.setTitle(fullTitle);
    this.tag('description', page.description);
    this.tag('robots', index ? 'index,follow' : 'noindex,nofollow');
    this.property('og:type', 'website');
    this.property('og:site_name', 'FIT360');
    this.property('og:title', fullTitle);
    this.property('og:description', page.description);
    this.property('og:url', url);
    this.property('og:image', image);
    this.tag('twitter:card', 'summary');
    this.tag('twitter:title', fullTitle);
    this.tag('twitter:description', page.description);
    this.canonical(index ? url : null);
  }

  /** Marca la página como privada (sin título propio): se usa en las rutas del panel. */
  noIndex(): void {
    this.tag('robots', 'noindex,nofollow');
    this.canonical(null);
    this.jsonLd(null);
  }

  /** Datos estructurados (JSON-LD) de la página; reemplaza los anteriores. */
  jsonLd(data: unknown): void {
    const head = this.document.head;
    head.querySelector('script[data-seo="jsonld"]')?.remove();
    if (data === null || data === undefined) {
      return;
    }
    const script = this.document.createElement('script');
    script.type = 'application/ld+json';
    script.setAttribute('data-seo', 'jsonld');
    // Se escapa «<» para que ningún texto pueda cerrar la etiqueta.
    script.textContent = JSON.stringify(data).replace(/</g, '\\u003c');
    head.appendChild(script);
  }

  private tag(name: string, content: string): void {
    this.meta.updateTag({ name, content });
  }

  private property(property: string, content: string): void {
    this.meta.updateTag({ property, content });
  }

  private canonical(href: string | null): void {
    const head = this.document.head;
    head.querySelector('link[rel="canonical"]')?.remove();
    if (href === null) {
      return;
    }
    const link = this.document.createElement('link');
    link.setAttribute('rel', 'canonical');
    link.setAttribute('href', href);
    head.appendChild(link);
  }
}
