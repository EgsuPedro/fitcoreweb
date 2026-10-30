import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

/**
 * Revisión automática de calidad de las páginas públicas y de cuenta contra la web compilada: accesibilidad (axe, reglas WCAG 2 A y AA), errores de consola y de la política
 * de seguridad de contenido, cabeceras de seguridad y peso de lo que descarga un visitante. No reemplaza una revisión manual con lector de pantalla.
 */
const publicPages = ['/', '/funcionalidades', '/planes', '/nosotros', '/preguntas-frecuentes', '/contacto', '/registro', '/terminos', '/privacidad', '/olvide-mi-clave', '/login'];
const tokenPages = ['/verificar-correo', '/restablecer-clave', '/aceptar-invitacion'];

test.describe('calidad del sitio público y de las pantallas de cuenta', () => {
  for (const path of [...publicPages, ...tokenPages]) {
    test(`accesibilidad (WCAG 2 A/AA) de ${path}`, async ({ page }) => {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
      const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
      const summary = results.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.length} elemento(s), p. ej. ${v.nodes[0]?.target.join(' ')}`);
      expect(summary, `${path}\n${summary.join('\n')}`).toEqual([]);
    });
  }

  test('el modo oscuro también cumple el contraste en las páginas principales', async ({ browser, baseURL }) => {
    const ctx = await browser.newContext({ baseURL, colorScheme: 'dark', locale: 'es-EC' });
    const page = await ctx.newPage();
    for (const path of ['/', '/planes', '/contacto', '/registro', '/login']) {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
      const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
      const summary = results.violations.map((v) => `${v.id}: ${v.nodes[0]?.target.join(' ')}`);
      expect(summary, `${path} (oscuro)`).toEqual([]);
    }
    await ctx.close();
  });

  test('ninguna página pública da errores de consola ni viola la política de seguridad de contenido', async ({ page }) => {
    const problems: string[] = [];
    page.on('console', (m) => {
      // Un 401 al intentar recuperar una sesión que no existe es esperado para un visitante.
      if ((m.type() === 'error' || m.type() === 'warning') && !/status of 401/.test(m.text())) problems.push(`${page.url()} → ${m.text()}`);
    });
    page.on('pageerror', (e) => problems.push(`${page.url()} → ${e.message}`));
    for (const path of publicPages) {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
    }
    expect(problems).toEqual([]);
  });

  test('las páginas públicas llevan las cabeceras de seguridad', async ({ request }) => {
    for (const path of ['/', '/planes', '/contacto', '/registro']) {
      const response = await request.get(path);
      const headers = response.headers();
      expect(headers['x-content-type-options'], path).toBe('nosniff');
      expect(headers['x-frame-options'], path).toBe('DENY');
      expect(headers['referrer-policy'], path).toBe('strict-origin-when-cross-origin');
      expect(headers['content-security-policy'], path).toContain("default-src 'self'");
      expect(headers['content-security-policy'], path).toContain("frame-ancestors 'none'");
      expect(headers['permissions-policy'], path).toContain('camera=()');
    }
  });

  test('los textos de las páginas públicas no dejan claves de traducción sin resolver en ningún idioma', async ({ page }) => {
    for (const lang of ['ES', 'EN']) {
      await page.goto('/');
      await page.waitForLoadState('networkidle');
      await page.getByRole('button', { name: lang, exact: true }).click();
      for (const path of publicPages.filter((p) => p !== '/login')) {
        await page.goto(path);
        await page.waitForLoadState('networkidle');
        const text = await page.locator('body').innerText();
        expect(text, `${path} (${lang})`).not.toMatch(/\b(site|errors|home|login|common)\.[a-zA-Z_]+\.[a-zA-Z_.]+\b/);
      }
    }
  });

  test('lo que descarga un visitante en la portada pesa poco y no incluye la animación de 7 MB', async ({ page }) => {
    let bytes = 0;
    const heavy: string[] = [];
    page.on('response', async (response) => {
      const length = Number(response.headers()['content-length'] ?? 0);
      bytes += length;
      if (length > 1_000_000) heavy.push(`${response.url()} ${length}`);
    });
    await page.goto('/');
    await page.waitForLoadState('networkidle');
    expect(heavy).toEqual([]);
    expect(bytes, `la portada descargó ${(bytes / 1024).toFixed(0)} kB`).toBeLessThan(400_000);
  });
});
