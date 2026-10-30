import { expect, test, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Configuración inicial contra API, Worker, PostgreSQL y Mailpit reales: el gimnasio se crea de verdad con el registro público (correo de confirmación incluido),
 * y el propietario recorre el asistente. Necesita el buzón de desarrollo en E2E_MAILPIT_URL (por defecto http://localhost:8025).
 */
const run = Date.now().toString(36);
const slug = `onb-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const mailpit = process.env['E2E_MAILPIT_URL'] ?? 'http://localhost:8025';

async function confirmationToken(email: string): Promise<string> {
  for (let attempt = 0; attempt < 30; attempt++) {
    const found = await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`);
    const list = (await found.json()) as { messages: { ID: string }[] };
    if (list.messages.length > 0) {
      const detail = (await (await fetch(`${mailpit}/api/v1/message/${list.messages[0].ID}`)).json()) as { Text: string };
      const match = /token=([A-Za-z0-9_\-.%]+)/.exec(detail.Text);
      if (match) {
        return decodeURIComponent(match[1]);
      }
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error('No llegó el correo de confirmación a Mailpit.');
}

test.describe.serial('configuración inicial del gimnasio', () => {
  let page: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const login = await api.post('/api/v1/auth/login', { data: { tenantSlug: 'platform', email: platform.email, password: platform.password, useCookie: false } });
    const token = ((await login.json()) as { accessToken: string }).accessToken;
    const headers = { Authorization: `Bearer ${token}` };

    const planCode = `onb-${run}`;
    const plan = await api.post('/api/v1/platform/plans', {
      headers,
      data: {
        code: planCode, name: `Plan ${run}`, isActive: true, currency: 'USD', billingPeriod: 'Monthly', price: 25, trialDays: 14, features: ['members', 'branches'],
        limits: [{ code: 'branches', value: 2 }, { code: 'internal_users', value: 5 }, { code: 'members', value: null }, { code: 'storage_mb', value: 500 }],
      },
    });
    expect(plan.ok(), await plan.text()).toBeTruthy();
    const planId = ((await plan.json()) as { id: string }).id;
    expect((await api.patch(`/api/v1/platform/plans/${planId}/visibility`, { headers, data: { isPublic: true } })).ok()).toBeTruthy();

    const registered = await api.post('/api/v1/public/registrations', {
      data: {
        ownerFullName: 'Dueña Asistente', email: ownerEmail, password: ownerPassword, gymName: `Gimnasio ${run}`, slug, planCode,
        termsVersion: '2026-10-30-borrador', privacyVersion: '2026-10-30-borrador', locale: 'es',
      },
    });
    expect(registered.status(), await registered.text()).toBe(202);
    const verified = await api.post('/api/v1/public/registrations/verify-email', { data: { token: await confirmationToken(ownerEmail) } });
    expect(verified.ok(), await verified.text()).toBeTruthy();
    await api.dispose();

    page = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(page, slug, ownerEmail, ownerPassword);
    await expect(nav(page)).toBeVisible();
  });

  test.afterAll(async () => {
    await page.context().close();
  });

  test('un gimnasio recién registrado ve el aviso y el asistente empieza por los datos del gimnasio', async () => {
    await expect(page.getByText('Su gimnasio está pendiente de configuración.')).toBeVisible();
    await page.locator('.setup-banner').getByRole('link', { name: 'Continuar configuración' }).click();
    await expect(page.getByRole('heading', { name: 'Configuración inicial', level: 1 })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Datos de su gimnasio' })).toBeVisible();
  });

  test('los datos inválidos se señalan y el avance se conserva al recargar la página', async () => {
    await page.getByLabel('Correo de contacto').fill('esto-no-es-un-correo');
    await page.getByRole('button', { name: 'Guardar y continuar' }).click();
    await expect(page.getByText('Revise los campos marcados.')).toBeVisible();

    await page.getByLabel('Correo de contacto').fill('info@gimnasio-prueba.test');
    await page.getByLabel('Teléfono de contacto').fill('+593 99 123 4567');
    await page.getByRole('button', { name: 'Guardar y continuar' }).click();
    await expect(page.getByRole('heading', { name: 'Su sucursal principal' })).toBeVisible();

    // El avance vive en el servidor: tras recargar vuelve al paso en el que se quedó.
    await page.reload();
    await expect(page.getByRole('heading', { name: 'Su sucursal principal' })).toBeVisible();
  });

  test('un horario con apertura posterior al cierre no se puede guardar; uno válido sí', async () => {
    await page.getByLabel('Nombre de la sucursal').fill('Sede Centro');
    await page.getByLabel('Dirección').fill('Av. Amazonas N24-03 y Colón');
    await page.getByLabel('Abre Lunes').fill('22:00');
    await page.getByRole('button', { name: 'Guardar y continuar' }).click();
    await expect(page.getByText('En los días abiertos la apertura debe ser anterior al cierre.')).toBeVisible();

    await page.getByLabel('Abre Lunes').fill('06:00');
    await page.getByRole('button', { name: 'Guardar y continuar' }).click();
    await expect(page.getByRole('heading', { name: 'Reglas de acceso con deuda' })).toBeVisible();
  });

  test('los pasos opcionales se pueden omitir y el resumen permite terminar', async () => {
    await page.getByRole('button', { name: 'Omitir por ahora' }).click();
    await expect(page.getByRole('heading', { name: 'Su equipo' })).toBeVisible();
    await page.getByRole('button', { name: 'Omitir por ahora' }).click();
    await expect(page.getByRole('heading', { name: 'Resumen' })).toBeVisible();
    await expect(page.getByText('Sede Centro')).toBeVisible();

    await page.getByRole('button', { name: 'Terminar configuración' }).click();
    await expect(page.getByText('¡Listo! Su gimnasio quedó configurado.')).toBeVisible();
  });

  test('al terminar desaparece el aviso del resto del sistema', async () => {
    await page.getByRole('link', { name: 'Ir al inicio' }).click();
    await expect(page.getByText('Su gimnasio está pendiente de configuración.')).toHaveCount(0);
  });
});
