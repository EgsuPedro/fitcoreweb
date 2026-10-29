import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/** Portal «Mi suscripción» contra API + PostgreSQL reales: el gimnasio pide un cambio de plan, la plataforma lo aprueba y el contrato cambia. */
const run = Date.now().toString(36);
const slug = `sub-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const planCode = `e2e-${run}`;
const planName = `Plan Pro ${run}`;

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

test.describe.serial('portal Mi suscripción', () => {
  let ownerPage: Page;
  let platformPage: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const token = await login(api, 'platform', platform.email, platform.password);
    const headers = { Authorization: `Bearer ${token}` };

    const features = ['members', 'branches', 'cash_payments', 'attendance', 'training', 'nutrition', 'classes', 'advanced_reports', 'exports', 'advanced_permissions', 'extra_storage'];
    const plan = await api.post('/api/v1/platform/plans', {
      headers,
      data: {
        code: planCode, name: planName, isActive: true, currency: 'USD', billingPeriod: 'Monthly', price: 30, trialDays: 0, features,
        limits: [{ code: 'branches', value: 5 }, { code: 'internal_users', value: null }, { code: 'members', value: 500 }, { code: 'storage_mb', value: null }],
      },
    });
    expect(plan.ok(), await plan.text()).toBeTruthy();

    const tenant = await api.post('/api/v1/platform/tenants', {
      headers,
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Suscripción', ownerPassword },
    });
    expect(tenant.ok(), await tenant.text()).toBeTruthy();
    await api.dispose();

    ownerPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(ownerPage, slug, ownerEmail, ownerPassword);
    await expect(nav(ownerPage)).toBeVisible();

    platformPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(platformPage, 'platform', platform.email, platform.password);
    await expect(nav(platformPage)).toBeVisible();
  });

  test.afterAll(async () => {
    await ownerPage.context().close();
    await platformPage.context().close();
  });

  test('el propietario ve su plan y el catálogo, y pide un cambio de plan sin que su contrato cambie', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Mi suscripción', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Mi suscripción', level: 1 })).toBeVisible();
    await expect(page.getByRole('heading', { name: /Plan heredado/ })).toBeVisible();

    await page.getByRole('tab', { name: 'Planes' }).click();
    const card = page.locator('section.card', { has: page.getByRole('heading', { name: planName }) });
    await expect(card.getByText('Es una mejora: entra de inmediato.')).toBeVisible();
    await card.getByRole('button', { name: 'Pedir este plan' }).click();

    const dialog = page.getByRole('dialog');
    await dialog.getByLabel('Mensaje para el equipo (opcional)').fill('Queremos abrir otra sucursal');
    await dialog.getByRole('button', { name: 'Enviar solicitud' }).click();
    await expect(page.getByText('Solicitud enviada. Le responderemos aquí.')).toBeVisible();

    // Pedirlo no cambia el contrato: sigue en el plan heredado y la solicitud queda pendiente.
    await page.getByRole('tab', { name: 'Resumen' }).click();
    await expect(page.getByRole('heading', { name: /Plan heredado/ })).toBeVisible();
    await expect(page.getByText(`→ ${planName}`)).toBeVisible();
  });

  test('la plataforma aprueba la solicitud y el gimnasio ve el plan nuevo y la respuesta', async () => {
    const admin = platformPage;
    await nav(admin).getByRole('link', { name: 'Solicitudes de gimnasios', exact: true }).click();
    const row = admin.getByRole('row', { name: new RegExp(`Gimnasio ${run}`) });
    await expect(row).toBeVisible();
    await row.getByRole('button', { name: 'Aprobar' }).click();
    await admin.getByRole('dialog').getByRole('button', { name: 'Confirmar' }).click();
    await expect(admin.getByText('Solicitud aprobada y aplicada.')).toBeVisible();

    const page = ownerPage;
    await page.reload();
    await expect(page.getByRole('heading', { name: new RegExp(planName) })).toBeVisible();
    await page.getByRole('tab', { name: 'Solicitudes' }).click();
    await expect(page.getByRole('row', { name: /Aprobada/ })).toBeVisible();
  });

  test('el rechazo exige una razón y el gimnasio la lee', async () => {
    const page = ownerPage;
    await page.getByRole('tab', { name: 'Resumen' }).click();
    await page.getByRole('button', { name: 'Pedir la cancelación' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button', { name: 'Enviar solicitud' }).click();
    await expect(dialog.getByText('Cuéntenos el motivo de la cancelación.')).toBeVisible();
    await dialog.getByLabel('Cuéntenos el motivo (obligatorio)').fill('Estamos evaluando otra opción');
    await dialog.getByRole('button', { name: 'Enviar solicitud' }).click();
    await expect(page.getByText('Solicitud enviada. Le responderemos aquí.')).toBeVisible();

    const admin = platformPage;
    await admin.reload();
    const row = admin.getByRole('row', { name: new RegExp(`Gimnasio ${run}`) });
    await row.getByRole('button', { name: 'Rechazar' }).click();
    const reject = admin.getByRole('dialog');
    await reject.getByRole('button', { name: 'Confirmar' }).click();
    await expect(reject.getByText('Indique la razón del rechazo: el gimnasio la verá.')).toBeVisible();
    await reject.getByLabel('Razón del rechazo (la verá el gimnasio)').fill('Hablemos primero por teléfono');
    await reject.getByRole('button', { name: 'Confirmar' }).click();
    await expect(admin.getByText('Solicitud rechazada.')).toBeVisible();

    await page.reload();
    await page.getByRole('tab', { name: 'Solicitudes' }).click();
    await expect(page.getByText('Hablemos primero por teléfono')).toBeVisible();
  });
});
