import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Configuración de la facturación electrónica contra API + PostgreSQL reales: el dueño guarda los datos del emisor, crea un punto de emisión
 * y carga un certificado .p12 de PRUEBA que se genera aquí mismo con openssl (autofirmado, sin ningún dato real).
 */
const run = Date.now().toString(36);
const slug = `inv-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const ruc = '1790000028001';
const certPassword = `Prueba-${run}-Clave`;

/** Crea un .p12 autofirmado en un directorio temporal y devuelve sus bytes; el directorio se borra siempre. */
function testP12(): Buffer {
  const dir = mkdtempSync(join(tmpdir(), 'fc-p12-'));
  try {
    const key = join(dir, 'key.pem');
    const cert = join(dir, 'cert.pem');
    const p12 = join(dir, 'cert.p12');
    execFileSync('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '365', '-subj', `/CN=Gimnasio Prueba/serialNumber=${ruc}`], { stdio: 'ignore' });
    execFileSync('openssl', ['pkcs12', '-export', '-inkey', key, '-in', cert, '-out', p12, '-passout', `pass:${certPassword}`], { stdio: 'ignore' });
    return readFileSync(p12);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

test.describe.serial('configuración de facturación electrónica', () => {
  let ownerPage: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const token = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${token}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Factura', ownerPassword },
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

  test('sin configurar, la pantalla lista lo que falta; con emisor, punto de emisión y certificado queda lista para emitir', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Facturación electrónica', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Facturación electrónica', level: 1 })).toBeVisible();
    await expect(page.getByText('Falta configuración')).toBeVisible();

    // Un RUC inválido se avisa antes de enviar.
    await page.getByLabel(/^RUC/).fill('1234');
    await page.getByLabel('Razón social').fill('Gimnasio Prueba S.A.');
    await page.getByLabel('Dirección de la matriz').fill('Av. Amazonas N24-03 y Colón, Quito');
    await page.getByLabel(/^Correo del emisor/).fill('facturas@gimnasio.test');
    await page.getByRole('button', { name: 'Guardar', exact: true }).first().click();
    await expect(page.getByText('Escriba un RUC de 13 dígitos que termine en 001.')).toBeVisible();

    await page.getByLabel(/^RUC/).fill(ruc);
    await page.getByRole('button', { name: 'Guardar', exact: true }).first().click();
    await expect(page.getByText('Configuración guardada.')).toBeVisible();

    await page.getByRole('button', { name: 'Nuevo punto de emisión' }).click();
    const dialog = page.getByRole('dialog', { name: 'Nuevo punto de emisión' });
    await dialog.getByLabel(/^Establecimiento/).fill('001');
    await dialog.getByLabel(/^Punto de emisión/).fill('001');
    await dialog.getByLabel('Dirección del establecimiento').fill('Av. Amazonas N24-03 y Colón, Quito');
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByRole('row', { name: /001\s+001/ })).toBeVisible();

    // Si el SRI ya tiene números de ese punto (otro sistema, pruebas anteriores), se indica el último usado: solo sube.
    await page.getByRole('row', { name: /001\s+001/ }).getByRole('button', { name: 'Secuencial' }).click();
    const sequence = page.getByRole('dialog', { name: 'Último secuencial usado' });
    await sequence.getByLabel(/^Último secuencial que el SRI/).fill('1000');
    await sequence.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByRole('row', { name: /001\s+001.*1000/ })).toBeVisible();
    await page.getByRole('row', { name: /001\s+001/ }).getByRole('button', { name: 'Secuencial' }).click();
    await sequence.getByLabel(/^Último secuencial que el SRI/).fill('999');
    await sequence.getByRole('button', { name: 'Guardar' }).click();
    await expect(sequence.getByText('El secuencial solo puede subir')).toBeVisible();
    await sequence.getByRole('button', { name: 'Cancelar' }).click();

    // Todavía falta el certificado: no está listo.
    await expect(page.getByText('Falta configuración')).toBeVisible();

    // Una contraseña equivocada se rechaza con un mensaje claro y no deja nada guardado.
    const p12 = testP12();
    const fileInput = page.locator('input[type=file]');
    await fileInput.setInputFiles({ name: 'prueba.p12', mimeType: 'application/x-pkcs12', buffer: p12 });
    await page.getByLabel(/^Contraseña del certificado/).fill('clave-equivocada');
    await page.getByRole('button', { name: 'Cargar certificado' }).click();
    await expect(page.getByText('La contraseña del certificado no es correcta o el archivo no es un .p12 válido.')).toBeVisible();

    await page.getByLabel(/^Contraseña del certificado/).fill(certPassword);
    await page.getByRole('button', { name: 'Cargar certificado' }).click();
    await expect(page.getByText('Certificado guardado.')).toBeVisible();
    await expect(page.getByText('Listo para emitir')).toBeVisible();
    await expect(page.getByText('Gimnasio Prueba').first()).toBeVisible();

    // La contraseña no queda en pantalla ni en el HTML.
    await expect(page.getByLabel(/^Contraseña del certificado/)).toHaveValue('');
    expect(await page.content()).not.toContain(certPassword);
  });

  test('el certificado se puede retirar y la facturación deja de estar lista', async () => {
    const page = ownerPage;
    await page.getByRole('button', { name: 'Retirar certificado' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Retirar certificado' }).click();
    await expect(page.getByText('Certificado retirado.')).toBeVisible();
    await expect(page.getByText('Falta configuración')).toBeVisible();
    await expect(page.getByText('Todavía no hay un certificado cargado.')).toBeVisible();
  });
});

