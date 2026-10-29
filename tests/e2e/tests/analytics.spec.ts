import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Analítica contra API + PostgreSQL reales: el propietario ve el panel con indicadores calculados por el backend, cambia el período y las
 * pestañas de los reportes, exporta a Excel y lo descarga desde su historial; un rol sin permisos de reportes no ve el menú ni entra por URL.
 */
const run = Date.now().toString(36);
const slug = `ana-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const deskEmail = `recepcion@${slug}.test`;
const deskPassword = 'Recepcion-Clave-12345';

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

test.describe.serial('analítica', () => {
  let ownerPage: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const platformToken = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${platformToken}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Analítica', ownerPassword },
    });
    expect(tenant.ok(), await tenant.text()).toBeTruthy();

    const auth = { Authorization: `Bearer ${await login(api, slug, ownerEmail, ownerPassword)}` };
    const branch = await api.post('/api/v1/branches', { headers: auth, data: { code: 'UIO', name: 'Quito Centro', timeZone: 'America/Guayaquil', sriEstablishmentCode: '001', address: null } });
    expect(branch.ok(), await branch.text()).toBeTruthy();
    const branchId = ((await branch.json()) as { id: string }).id;

    const member = await api.post('/api/v1/members', {
      headers: auth,
      data: { branchId, firstName: 'Lucía', lastName: 'Andrade', idType: 'Cedula', idNumber: '1710034065', email: `lucia@${slug}.test`, phone: '0991234567', birthDate: '1992-03-04' },
    });
    expect(member.ok(), await member.text()).toBeTruthy();

    const roles = (await (await api.get('/api/v1/roles', { headers: auth })).json()) as { id: string; name: string }[];
    const desk = await api.post('/api/v1/users', {
      headers: auth,
      data: { email: deskEmail, fullName: 'Nora Nutricionista', password: deskPassword, roleIds: [roles.find((r) => r.name === 'Nutricionista')!.id], branchIds: [branchId] },
    });
    expect(desk.ok(), await desk.text()).toBeTruthy();
    await api.dispose();

    ownerPage = await (await browser.newContext({ baseURL, locale: 'es-EC', acceptDownloads: true })).newPage();
    await signIn(ownerPage, slug, ownerEmail, ownerPassword);
  });

  test.afterAll(async () => {
    await ownerPage.context().close();
  });

  test('el propietario ve el panel con indicadores reales y su definición', async () => {
    await nav(ownerPage).getByRole('link', { name: 'Panel de analítica', exact: true }).click();
    await expect(ownerPage.getByRole('heading', { name: 'Panel de analítica', level: 1 })).toBeVisible();

    const newMembers = ownerPage.locator('.kpi', { hasText: 'Alumnos nuevos' }).first();
    await expect(newMembers).toBeVisible();
    await expect(newMembers.locator('.kpi-value')).toHaveText('1');

    // Sin cobros no hay base de comparación: nunca se muestra un porcentaje engañoso.
    const collected = ownerPage.locator('.kpi', { hasText: 'Total cobrado' }).first();
    await expect(collected).toBeVisible();
    await collected.locator('summary').click();
    await expect(collected.getByText(/Pagos confirmados recibidos/)).toBeVisible();
  });

  test('cambiar el período recarga los indicadores y valida el rango personalizado', async () => {
    await ownerPage.locator('.filters select').selectOption('Last7Days');
    await expect(ownerPage.getByText(/Período aplicado \(7 días\)/)).toBeVisible();

    await ownerPage.locator('.filters select').selectOption('Custom');
    await ownerPage.locator('.filters input[type=date]').first().fill('2026-10-20');
    await ownerPage.locator('.filters input[type=date]').last().fill('2026-10-10');
    await expect(ownerPage.getByText('La fecha inicial no puede ser posterior a la final.').first()).toBeVisible();

    // Los filtros se comparten entre reportes: se vuelve a un período válido.
    await ownerPage.locator('.filters select').selectOption('Last30Days');
    await expect(ownerPage.getByText(/Período aplicado \(30 días\)/)).toBeVisible();
  });

  test('las pestañas de reportes navegan y conservan la sucursal', async () => {
    await nav(ownerPage).getByRole('link', { name: 'Reportes financieros', exact: true }).click();
    await expect(ownerPage.getByRole('heading', { name: 'Reportes financieros', level: 1 })).toBeVisible();
    await ownerPage.getByRole('tab', { name: 'Saldos pendientes' }).click();
    await expect(ownerPage.getByText(/foto de hoy/).first()).toBeVisible();
    await ownerPage.getByRole('tab', { name: 'Caja' }).click();
    await expect(ownerPage.getByRole('tab', { name: 'Caja', selected: true })).toBeVisible();

    await nav(ownerPage).getByRole('link', { name: 'Comparar sucursales', exact: true }).click();
    await expect(ownerPage.getByRole('row', { name: /Quito Centro Sin datos/ })).toBeVisible();
  });

  test('exporta a Excel y lo descarga desde el historial', async () => {
    await nav(ownerPage).getByRole('link', { name: 'Reportes de alumnos', exact: true }).click();
    await ownerPage.getByRole('tab', { name: 'Altas y bajas' }).click();

    const download = ownerPage.waitForEvent('download');
    await ownerPage.getByRole('button', { name: 'Exportar a Excel' }).click();
    expect((await download).suggestedFilename()).toMatch(/\.xlsx$/);

    await nav(ownerPage).getByRole('link', { name: 'Mis exportaciones', exact: true }).click();
    const row = ownerPage.getByRole('row', { name: /Membresías vendidas/ }).first();
    await expect(row.getByText('Disponible')).toBeVisible();

    const again = ownerPage.waitForEvent('download');
    await row.getByRole('button', { name: 'Descargar' }).click();
    expect((await again).suggestedFilename()).toMatch(/\.xlsx$/);
  });

  test('un rol sin permisos de reportes no ve el menú ni entra por URL', async ({ browser, baseURL }) => {
    const page = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    try {
      await signIn(page, slug, deskEmail, deskPassword);
      await expect(nav(page)).toBeVisible();
      await expect(nav(page).getByRole('link', { name: 'Reportes financieros', exact: true })).toHaveCount(0);
      await expect(nav(page).getByRole('link', { name: 'Mis exportaciones', exact: true })).toHaveCount(0);

      await page.goto('/analytics/financial');
      await expect(page.getByRole('heading', { name: 'Reportes financieros', level: 1 })).toHaveCount(0);
    } finally {
      await page.context().close();
    }
  });
});
