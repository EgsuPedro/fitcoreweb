import { deflateSync, crc32 } from 'node:zlib';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/** Logo del gimnasio contra API + PostgreSQL reales: se carga desde su pantalla, aparece en el menú y los reportes se exportan a Excel. */
const run = Date.now().toString(36);
const slug = `brd-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';

/** PNG de un color, armado a mano (sin bibliotecas de imágenes). */
function png(width: number, height: number): Buffer {
  const chunk = (type: string, data: Buffer) => {
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data]);
    const length = Buffer.alloc(4);
    length.writeUInt32BE(data.length);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body) >>> 0);
    return Buffer.concat([length, body, crc]);
  };
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8;
  header[9] = 2;
  const row = Buffer.concat([Buffer.from([0]), Buffer.alloc(width * 3, 0x70)]);
  const raw = Buffer.concat(Array.from({ length: height }, () => row));
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', header), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

test.describe.serial('logo del gimnasio', () => {
  let ownerPage: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const token = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${token}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Marca', ownerPassword },
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

  test('un archivo que no es PNG o JPG se rechaza antes de subirlo y uno válido se guarda y aparece en el menú', async () => {
    const page = ownerPage;
    // La vista previa y el logo del menú son imágenes locales (blob:): la política de seguridad del servidor debe permitirlas.
    const violations: string[] = [];
    page.on('console', (message) => {
      if (/content security policy/i.test(message.text())) violations.push(message.text());
    });
    await nav(page).getByRole('link', { name: 'Logo del gimnasio', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Logo del gimnasio', level: 1 })).toBeVisible();
    await expect(page.locator('.tenant-logo')).toHaveCount(0);

    const input = page.locator('input[type="file"]');
    await input.setInputFiles({ name: 'logo.svg', mimeType: 'image/svg+xml', buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"/>') });
    await expect(page.getByText('Solo se admiten imágenes PNG o JPG.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Guardar logo' })).toBeDisabled();

    await input.setInputFiles({ name: 'diminuto.png', mimeType: 'image/png', buffer: png(16, 16) });
    await expect(page.getByText('La imagen debe medir entre 32 y 2000 píxeles por lado.')).toBeVisible();

    await input.setInputFiles({ name: 'logo.png', mimeType: 'image/png', buffer: png(160, 80) });
    await expect(page.getByRole('img', { name: 'Vista previa del logo' })).toBeVisible();
    await page.getByRole('button', { name: 'Guardar logo' }).click();
    await expect(page.getByText('Logo guardado.')).toBeVisible();
    await expect(page.locator('.tenant-logo')).toBeVisible();
    // Se dibujó de verdad (no una imagen rota): tiene ancho natural.
    expect(await page.locator('.tenant-logo').evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
    expect(violations).toEqual([]);
  });

  test('el reporte se exporta a Excel con el logo y el CSV sigue disponible', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Reportes de alumnos', exact: true }).click();
    await page.getByRole('tab', { name: 'Altas y bajas' }).click();

    const excel = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Exportar a Excel' }).click();
    expect((await excel).suggestedFilename()).toMatch(/\.xlsx$/);

    const csv = page.waitForEvent('download');
    await page.getByRole('button', { name: 'CSV', exact: true }).click();
    expect((await csv).suggestedFilename()).toMatch(/\.csv$/);
  });

  test('quitar el logo lo retira del menú', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Logo del gimnasio', exact: true }).click();
    await page.getByRole('button', { name: 'Quitar logo' }).first().click();
    await page.getByRole('dialog').getByRole('button', { name: 'Quitar logo' }).click();
    await expect(page.getByText('Logo eliminado.')).toBeVisible();
    await expect(page.locator('.tenant-logo')).toHaveCount(0);
  });
});
