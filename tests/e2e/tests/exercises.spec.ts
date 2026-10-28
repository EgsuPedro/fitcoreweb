import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/** Biblioteca de ejercicios contra API + PostgreSQL reales: catálogo base de solo lectura, ejercicios propios y catálogo de plataforma. */
const run = Date.now().toString(36);
const slug = `ejr-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const globalName = `Global ${run}`;

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

test.describe.serial('biblioteca de ejercicios', () => {
  let ownerPage: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const token = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${token}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Ejercicios', ownerPassword },
    });
    expect(tenant.ok(), await tenant.text()).toBeTruthy();
    await api.dispose();

    ownerPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(ownerPage, slug, ownerEmail, ownerPassword);
    await expect(nav(ownerPage)).toBeVisible();
  });

  test.afterAll(async () => {
    await ownerPage.context().close();
  });

  test('el catálogo base se puede consultar y filtrar pero no editar, y se puede duplicar', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Ejercicios' }).click();
    await expect(page.getByText(/valídelo con su entrenador/)).toBeVisible();

    await page.getByPlaceholder('Buscar ejercicio por nombre…').fill('plancha');
    const row = page.getByRole('row', { name: /Plancha frontal/ });
    await expect(row).toBeVisible();
    await expect(row.getByText('Catálogo base')).toBeVisible();
    // El catálogo base no ofrece editar ni desactivar a un gimnasio.
    await expect(row.getByRole('button', { name: 'Editar' })).toHaveCount(0);
    await expect(row.getByRole('button', { name: 'Desactivar' })).toHaveCount(0);

    await row.getByRole('button', { name: 'Ver' }).click();
    const detail = page.getByRole('dialog');
    await expect(detail.getByText(/valide este ejercicio con su entrenador/)).toBeVisible();
    await expect(detail.getByRole('heading', { name: 'Instrucciones paso a paso' })).toBeVisible();
    await detail.getByRole('button', { name: 'Cerrar' }).last().click();

    await row.getByRole('button', { name: 'Duplicar' }).click();
    await expect(page.getByText('Copia creada: ya puede editarla.')).toBeVisible();
    await page.getByPlaceholder('Buscar ejercicio por nombre…').fill('plancha frontal (copia)');
    await expect(page.getByRole('row', { name: /Plancha frontal \(copia\)/ }).getByRole('button', { name: 'Editar' })).toBeVisible();
  });

  test('crear, validar el enlace, editar y desactivar un ejercicio propio', async () => {
    const page = ownerPage;
    await page.getByPlaceholder('Buscar ejercicio por nombre…').fill('');
    await page.getByRole('button', { name: 'Nuevo ejercicio' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.locator('[formControlName="name"]').fill('Remo con banda');
    await dialog.locator('[formControlName="primaryMuscle"]').selectOption('Back');
    await dialog.locator('[formControlName="equipment"]').selectOption('Band');
    await dialog.locator('[formControlName="trackingMode"]').selectOption('Reps');
    await dialog.getByRole('checkbox', { name: 'Bíceps' }).check();
    await dialog.locator('[formControlName="instructions"]').fill('Fije la banda\nTire hacia el abdomen');

    // Un enlace que no es https no se envía.
    await dialog.locator('[formControlName="videoUrl"]').fill('http://inseguro.test/video');
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(dialog.getByRole('alert')).toContainText('https://');
    await expect(page.getByText('Ejercicio guardado.')).toHaveCount(0);

    await dialog.locator('[formControlName="videoUrl"]').fill('https://ejemplo.com/remo');
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Ejercicio guardado.')).toBeVisible();

    await page.getByPlaceholder('Buscar ejercicio por nombre…').fill('remo con banda');
    const row = page.getByRole('row', { name: /Remo con banda/ });
    await expect(row).toBeVisible();
    await row.getByRole('button', { name: 'Editar' }).click();
    await page.getByRole('dialog').locator('[formControlName="difficulty"]').selectOption('Intermediate');
    await page.getByRole('dialog').getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Ejercicio guardado.')).toBeVisible();
    await expect(row.getByText('Intermedio')).toBeVisible();

    // Un nombre repetido (sin distinguir mayúsculas) lo rechaza el servidor.
    await page.getByRole('button', { name: 'Nuevo ejercicio' }).click();
    const again = page.getByRole('dialog');
    await again.locator('[formControlName="name"]').fill('REMO CON BANDA');
    await again.getByRole('button', { name: 'Guardar' }).click();
    await expect(again.getByRole('alert')).toContainText('Ya existe un ejercicio con ese nombre');
    await again.getByRole('button', { name: 'Cancelar' }).click();

    await row.getByRole('button', { name: 'Desactivar' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Desactivar' }).click();
    await expect(row.getByText('Inactivo')).toBeVisible();
  });

  test('la plataforma administra el catálogo base y el gimnasio lo ve sin poder editarlo', async ({ browser, baseURL }) => {
    const credentials = platformCredentials();
    const platformPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(platformPage, 'platform', credentials.email, credentials.password);
    await nav(platformPage).getByRole('link', { name: 'Catálogo de ejercicios' }).click();
    await expect(platformPage.getByRole('heading', { name: 'Catálogo base de ejercicios' })).toBeVisible();
    await platformPage.getByRole('button', { name: 'Nuevo ejercicio' }).click();
    const dialog = platformPage.getByRole('dialog');
    await dialog.locator('[formControlName="name"]').fill(globalName);
    await dialog.locator('[formControlName="primaryMuscle"]').selectOption('Glutes');
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(platformPage.getByText('Ejercicio guardado.')).toBeVisible();
    await platformPage.context().close();

    await ownerPage.reload();
    await ownerPage.getByPlaceholder('Buscar ejercicio por nombre…').fill(globalName);
    const row = ownerPage.getByRole('row', { name: new RegExp(globalName) });
    await expect(row).toBeVisible();
    await expect(row.getByText('Catálogo base')).toBeVisible();
    await expect(row.getByRole('button', { name: 'Editar' })).toHaveCount(0);
  });
});
