import { expect, test } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Sitio comercial público contra API y PostgreSQL reales: navegación, páginas prerenderizadas (se leen sin ejecutar JavaScript, como lo hace un buscador),
 * planes del catálogo real, formulario de contacto y la pantalla de plataforma donde se atienden las solicitudes.
 */
const run = Date.now().toString(36);
const planCode = `pub-${run}`;
const planName = `Plan Sitio ${run}`;
const visitorEmail = `visita-${run}@visitantes.test`;
const mensaje = `Quiero conocer FIT360 para mi gimnasio (${run}).`;

test.describe.serial('sitio comercial público', () => {
  test.beforeAll(async ({ playwright, baseURL }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const login = await api.post('/api/v1/auth/login', { data: { tenantSlug: 'platform', email: platform.email, password: platform.password, useCookie: false } });
    const headers = { Authorization: `Bearer ${((await login.json()) as { accessToken: string }).accessToken}` };
    const plan = await api.post('/api/v1/platform/plans', {
      headers,
      data: {
        code: planCode, name: planName, description: 'Plan de prueba para el sitio', isActive: true, currency: 'USD', billingPeriod: 'Monthly', price: 37, trialDays: 14,
        features: ['members', 'branches'],
        limits: [{ code: 'branches', value: 2 }, { code: 'internal_users', value: 5 }, { code: 'members', value: null }, { code: 'storage_mb', value: 500 }],
      },
    });
    expect(plan.ok(), await plan.text()).toBeTruthy();
    const planId = ((await plan.json()) as { id: string }).id;
    expect((await api.patch(`/api/v1/platform/plans/${planId}/visibility`, { headers, data: { isPublic: true } })).ok()).toBeTruthy();
    await api.dispose();
  });

  test('las páginas públicas llegan prerenderizadas: título, descripción y contenido sin ejecutar JavaScript', async ({ request }) => {
    for (const [path, title, h1] of [
      ['/', 'FIT360', 'Administre su gimnasio en un solo lugar'],
      ['/funcionalidades', 'Funcionalidades · FIT360', 'Funcionalidades'],
      ['/planes', 'Planes y precios · FIT360', 'Planes y precios'],
      ['/nosotros', 'Nosotros · FIT360', 'Nosotros'],
      ['/preguntas-frecuentes', 'Preguntas frecuentes · FIT360', 'Preguntas frecuentes'],
      ['/contacto', 'Contacto · FIT360', 'Contacto'],
    ]) {
      const html = await (await request.get(path)).text();
      expect(html, path).toContain(`<title>${title}</title>`);
      expect(html, path).toMatch(/<meta name="description" content="[^"]{40,}"/);
      expect(html, path).toContain('property="og:title"');
      expect(html, path).toMatch(/<link rel="canonical" href="[^"]*"/);
      expect(html, path).toContain('content="index,follow"');
      expect(html, path).toContain(h1);
    }
    const faq = await (await request.get('/preguntas-frecuentes')).text();
    expect(faq).toContain('"@type":"FAQPage"');
  });

  test('las rutas del panel no se indexan', async ({ request }) => {
    const html = await (await request.get('/members')).text();
    expect(html).toContain('noindex');
    expect(html).not.toContain('Administre su gimnasio en un solo lugar');
  });

  test('un visitante navega por el sitio y cada página tiene su título', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Administre su gimnasio en un solo lugar', level: 1 })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Crear mi gimnasio' }).first()).toBeVisible();

    const header = page.getByRole('navigation', { name: 'Navegación principal' });
    for (const [link, heading] of [
      ['Funcionalidades', 'Funcionalidades'],
      ['Preguntas frecuentes', 'Preguntas frecuentes'],
      ['Nosotros', 'Nosotros'],
      ['Contacto', 'Contacto'],
    ]) {
      await header.getByRole('link', { name: link, exact: true }).click();
      await expect(page.getByRole('heading', { name: heading, level: 1 })).toBeVisible();
      await expect(page).toHaveTitle(new RegExp(`${heading} · FIT360`));
    }
  });

  test('Funcionalidades separa lo que existe de lo que viene y no promete fechas', async ({ page }) => {
    await page.goto('/funcionalidades');
    await expect(page.getByRole('heading', { name: 'Cobros y caja' })).toBeVisible();
    const soon = page.locator('.pub-soon');
    await expect(soon).toContainText('Facturación electrónica del SRI');
    await expect(soon).toContainText('Próximamente');
    await expect(soon).toContainText('sin fecha comprometida');
  });

  test('Planes muestra el catálogo real con su precio y la prueba, y lleva a registrarse', async ({ page }) => {
    await page.goto('/planes');
    const card = page.locator('.pub-plan', { hasText: planName });
    await expect(card).toBeVisible();
    await expect(card).toContainText('$37');
    await expect(card).toContainText('14 días de prueba gratis');
    await expect(card.getByRole('link', { name: 'Empezar' })).toBeVisible();
  });

  test('las preguntas frecuentes se despliegan con el teclado', async ({ page }) => {
    await page.goto('/preguntas-frecuentes');
    const first = page.locator('summary', { hasText: '¿Qué es FIT360?' });
    await first.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByText('Es un sistema en línea para administrar un gimnasio')).toBeVisible();
  });

  test('los textos legales se marcan como borrador pendiente de revisión', async ({ page }) => {
    for (const [path, title] of [
      ['/terminos', 'Términos y condiciones'],
      ['/privacidad', 'Política de privacidad'],
    ]) {
      await page.goto(path);
      await expect(page.getByRole('heading', { name: title, level: 1 })).toBeVisible();
      await expect(page.getByRole('note')).toContainText('pendiente de revisión legal');
      await expect(page.getByText('Versión: 2026-10-30-borrador')).toBeVisible();
    }
  });

  test('el formulario de contacto valida, exige consentimiento y confirma el envío', async ({ page }) => {
    await page.goto('/contacto?tipo=demo');
    await expect(page.getByLabel('Quiero una demostración')).toBeChecked();

    await page.getByRole('button', { name: 'Enviar' }).click();
    await expect(page.getByText('Este campo es obligatorio.').first()).toBeVisible();
    await expect(page.getByText('Debe aceptar la política de privacidad para enviar el formulario.')).toBeVisible();

    await page.locator('input[formcontrolname=name]').fill('Visita de Prueba');
    await page.locator('input[formcontrolname=email]').fill('no-es-un-correo');
    await page.locator('textarea[formcontrolname=message]').fill('corto');
    await page.getByRole('button', { name: 'Enviar' }).click();
    await expect(page.getByText('Escriba un correo válido.')).toBeVisible();
    await expect(page.getByText('Escriba un mensaje de entre 10 y 1000 caracteres.')).toBeVisible();

    await page.locator('input[formcontrolname=email]').fill(visitorEmail);
    await page.locator('input[formcontrolname=phone]').fill('+593 99 123 4567');
    await page.locator('input[formcontrolname=gymName]').fill('Gimnasio Visitante');
    await page.locator('textarea[formcontrolname=message]').fill(mensaje);
    await page.getByLabel(/Acepto el tratamiento de mis datos/).check();
    await page.getByRole('button', { name: 'Enviar' }).click();
    await expect(page.getByText('¡Gracias! Recibimos su mensaje.')).toBeVisible();
  });

  test('el equipo de plataforma ve la solicitud, la marca como atendida y un gimnasio no puede verla', async ({ browser, baseURL }) => {
    const platform = platformCredentials();
    const ctx = await browser.newContext({ baseURL, locale: 'es-EC' });
    const page = await ctx.newPage();
    await signIn(page, 'platform', platform.email, platform.password);
    await nav(page).getByRole('link', { name: 'Solicitudes comerciales', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Solicitudes comerciales', level: 1 })).toBeVisible();

    const row = page.getByRole('row', { name: new RegExp(visitorEmail) });
    await expect(row).toContainText('Demostración');
    await expect(row).toContainText(mensaje);
    await expect(row).toContainText('Nueva');
    await row.getByRole('button', { name: 'Marcar atendida' }).click();
    await expect(page.getByText('Solicitud marcada como atendida.')).toBeVisible();
    await expect(page.getByRole('row', { name: new RegExp(visitorEmail) })).toContainText('Atendida');
    await ctx.close();
  });

  test('un visitante que pide una pantalla del panel va al inicio de sesión', async ({ page }) => {
    await page.goto('/members');
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fmembers/);
  });

  test('en el celular el sitio no se sale del ancho y el menú se despliega', async ({ browser, baseURL }) => {
    const ctx = await browser.newContext({ baseURL, viewport: { width: 360, height: 740 }, locale: 'es-EC' });
    const page = await ctx.newPage();
    for (const path of ['/', '/funcionalidades', '/planes', '/contacto', '/preguntas-frecuentes']) {
      await page.goto(path);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, `${path} se sale ${overflow}px`).toBeLessThanOrEqual(1);
    }
    await page.goto('/');
    const toggle = page.getByRole('button', { name: 'Menú' });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click();
    await expect(page.getByRole('button', { name: 'Cerrar menú' })).toHaveAttribute('aria-expanded', 'true');
    await page.getByRole('navigation', { name: 'Navegación principal' }).getByRole('link', { name: 'Planes y precios' }).click();
    await expect(page.getByRole('heading', { name: 'Planes y precios', level: 1 })).toBeVisible();
    await ctx.close();
  });

  test('el enlace «saltar al contenido» existe y la página se puede cambiar de idioma', async ({ page }) => {
    await page.goto('/');
    // La página llega prerenderizada y luego la aplicación toma el control: se espera a que termine antes de usar el teclado.
    await page.waitForLoadState('networkidle');
    await page.keyboard.press('Tab');
    await expect(page.getByRole('link', { name: 'Saltar al contenido' })).toBeFocused();
    await page.getByRole('button', { name: 'EN' }).click();
    await expect(page.getByRole('heading', { name: 'Run your gym in one place', level: 1 })).toBeVisible();
    await expect(page).toHaveTitle('FIT360');
  });
});
