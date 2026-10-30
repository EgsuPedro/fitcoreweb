import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { LEGAL_VERSION, PRIVACY_SECTIONS, TERMS_SECTIONS } from '../features/public/legal-texts';
import { hasPathMatch } from './auth/guards';
import { Seo } from './seo.service';

describe('Seo', () => {
  let seo: Seo;
  let doc: Document;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    seo = TestBed.inject(Seo);
    doc = TestBed.inject(DOCUMENT);
    doc.head.querySelectorAll('meta[name], meta[property], link[rel="canonical"], script[data-seo]').forEach((el) => el.remove());
  });

  const meta = (selector: string) => doc.head.querySelector(selector)?.getAttribute('content');

  it('una página pública lleva título con la marca, descripción, Open Graph, Twitter, canónica y se indexa', () => {
    seo.set({ title: 'Planes y precios', description: 'Los planes de FIT360.', path: '/planes' });

    expect(doc.title).toBe('Planes y precios · FIT360');
    expect(meta('meta[name="description"]')).toBe('Los planes de FIT360.');
    expect(meta('meta[name="robots"]')).toBe('index,follow');
    expect(meta('meta[property="og:title"]')).toBe('Planes y precios · FIT360');
    expect(meta('meta[property="og:type"]')).toBe('website');
    expect(meta('meta[name="twitter:card"]')).toBe('summary');
    expect(meta('meta[property="og:image"]')).toMatch(/\/brand\/logo\.png$/);
    expect(doc.head.querySelector('link[rel="canonical"]')?.getAttribute('href')).toMatch(/\/planes$/);
  });

  it('la portada se titula solo FIT360 y su canónica termina en la raíz', () => {
    seo.set({ title: 'FIT360', description: 'Inicio', path: '/' });

    expect(doc.title).toBe('FIT360');
    expect(doc.head.querySelector('link[rel="canonical"]')?.getAttribute('href')).toMatch(/\/$/);
  });

  it('una página privada lleva noindex y no tiene canónica', () => {
    seo.set({ title: 'X', description: 'Y', path: '/x' });
    seo.noIndex();

    expect(meta('meta[name="robots"]')).toBe('noindex,nofollow');
    expect(doc.head.querySelector('link[rel="canonical"]')).toBeNull();
  });

  it('cambiar de página reemplaza la canónica anterior en lugar de acumularla', () => {
    seo.set({ title: 'A', description: 'a', path: '/a' });
    seo.set({ title: 'B', description: 'b', path: '/b' });

    expect(doc.head.querySelectorAll('link[rel="canonical"]')).toHaveLength(1);
  });

  it('los datos estructurados se reemplazan, se pueden quitar y no pueden cerrar la etiqueta', () => {
    seo.jsonLd({ '@type': 'FAQPage', text: '</script><script>alert(1)</script>' });
    seo.jsonLd({ '@type': 'Other' });

    const scripts = doc.head.querySelectorAll('script[data-seo="jsonld"]');
    expect(scripts).toHaveLength(1);
    expect(scripts[0].textContent).toContain('Other');

    seo.jsonLd({ text: '</script>' });
    expect(doc.head.querySelector('script[data-seo="jsonld"]')?.textContent).not.toContain('</script>');
    seo.jsonLd(null);
    expect(doc.head.querySelector('script[data-seo="jsonld"]')).toBeNull();
  });
});

describe('páginas públicas', () => {
  it('«/» sin más ruta no coincide con el marco de las páginas internas; una ruta con segmentos sí', () => {
    expect(hasPathMatch({} as never, [], {} as never)).toBe(false);
    expect(hasPathMatch({} as never, [{ path: "planes" } as never], {} as never)).toBe(true);
  });

  it('los textos legales son un borrador versionado y completo', () => {
    expect(LEGAL_VERSION).toBe('2026-10-30-borrador');
    expect(TERMS_SECTIONS.length).toBeGreaterThanOrEqual(6);
    expect(PRIVACY_SECTIONS.length).toBeGreaterThanOrEqual(5);
    for (const section of [...TERMS_SECTIONS, ...PRIVACY_SECTIONS]) {
      expect(section.heading.length).toBeGreaterThan(0);
      expect(section.paragraphs.length).toBeGreaterThan(0);
    }
  });
});
