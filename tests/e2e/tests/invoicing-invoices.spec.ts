import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Facturas electrónicas contra API + PostgreSQL reales: facturar una venta cobrada desde la ficha del alumno, ver el comprobante, descartarlo con motivo y volver a emitir la misma venta.
 * Solo se EMITE (sin enviar): la prueba nunca toca al SRI. Todo con datos de prueba y un certificado autofirmado generado aquí con openssl.
 */
const run = Date.now().toString(36);
const slug = `fac-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const ruc = '1790000028001';
const certPassword = `Prueba-${run}-Clave`;
const memberCedula = '1712345675';

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

test.describe.serial('facturas electrónicas', () => {
  let ownerPage: Page;
  let memberId = '';

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const platformToken = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${platformToken}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Factura', ownerPassword },
    });
    expect(tenant.ok(), await tenant.text()).toBeTruthy();

    const auth = { Authorization: `Bearer ${await login(api, slug, ownerEmail, ownerPassword)}` };
    const branch = await api.post('/api/v1/branches', { headers: auth, data: { code: 'UIO', name: 'Quito Centro', timeZone: 'America/Guayaquil', sriEstablishmentCode: '001', address: null } });
    expect(branch.ok(), await branch.text()).toBeTruthy();
    const branchId = ((await branch.json()) as { id: string }).id;

    // Configuración de facturación lista (sin enviar nada al SRI).
    const issuer = await api.put('/api/v1/invoicing/issuer', {
      headers: auth,
      data: {
        ruc, legalName: 'Gimnasio Prueba S.A.', tradeName: 'Gimnasio Prueba', headOfficeAddress: 'Av. Amazonas N24-03 y Colón, Quito', taxRegime: 'General', accountingRequired: true,
        specialTaxpayerNumber: null, withholdingAgentNumber: null, environment: 'Test', notificationEmail: 'facturas@gimnasio.test', confirmProduction: false, cardPaymentMethod: 'Credit',
      },
    });
    expect(issuer.ok(), await issuer.text()).toBeTruthy();
    const point = await api.post('/api/v1/invoicing/emission-points', {
      headers: auth,
      data: { branchId: null, establishmentCode: '001', pointCode: '001', establishmentAddress: 'Av. Amazonas N24-03 y Colón, Quito' },
    });
    expect(point.ok(), await point.text()).toBeTruthy();
    const certificate = await api.put('/api/v1/invoicing/certificate', {
      headers: auth,
      multipart: { file: { name: 'prueba.p12', mimeType: 'application/x-pkcs12', buffer: testP12() }, password: certPassword },
    });
    expect(certificate.ok(), await certificate.text()).toBeTruthy();

    // Un alumno con una venta cobrada.
    const member = await api.post('/api/v1/members', {
      headers: auth,
      data: {
        branchId, firstName: 'Lucía', lastName: 'Andrade', idType: 'Cedula', idNumber: memberCedula, birthDate: '1992-03-04', email: 'lucia@correo.test', phone: '0991234567', address: 'Av. Principal 123',
      },
    });
    expect(member.ok(), await member.text()).toBeTruthy();
    memberId = ((await member.json()) as { id: string }).id;
    const obligation = await api.post('/api/v1/payment-obligations', { headers: auth, data: { memberId, concept: 'Membresía mensual', amount: 30, taxKind: 'General' } });
    expect(obligation.ok(), await obligation.text()).toBeTruthy();
    const obligationId = ((await obligation.json()) as { id: string }).id;
    const payment = await api.post('/api/v1/payments', {
      headers: { ...auth, 'Idempotency-Key': `pago-${run}` },
      data: { memberId, method: 'Card', amount: 30, reference: 'VCH-1234', bankName: null, methodDetail: null, paidAt: null, notes: 'Pago de prueba', allocations: [{ obligationId, amount: 30 }] },
    });
    expect(payment.ok(), await payment.text()).toBeTruthy();
    await api.dispose();

    ownerPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(ownerPage, slug, ownerEmail, ownerPassword);
    await expect(nav(ownerPage)).toBeVisible();
  });

  test.afterAll(async () => {
    await ownerPage.context().close();
  });

  test('sin facturas, el panel está en cero; desde la ficha del alumno se factura la venta cobrada', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Facturación electrónica', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Facturas electrónicas', level: 1 })).toBeVisible();
    await expect(page.getByText('Todavía no hay facturas electrónicas.')).toBeVisible();
    await expect(page.getByText('Autorizadas este mes')).toBeVisible();

    await page.goto(`/members/${memberId}`);
    await page.getByRole('row', { name: /Membresía mensual/ }).getByRole('link', { name: 'Facturar' }).click();
    await expect(page.getByRole('heading', { name: 'Emitir factura electrónica', level: 1 })).toBeVisible();
    await expect(page.getByText('Lucía Andrade')).toBeVisible();
    await expect(page.getByText(memberCedula)).toBeVisible();
    await expect(page.getByText('IVA 15%')).toBeVisible();

    // Solo emitir: asigna número y clave pero no toca al SRI.
    await page.getByRole('button', { name: 'Solo emitir (enviar después)' }).click();
    await expect(page.getByRole('heading', { name: /^Factura 001-001-/, level: 1 })).toBeVisible();
    await expect(page.getByText('Por firmar').first()).toBeVisible();
    await expect(page.getByText('lucia@correo.test')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Enviar al SRI' })).toBeVisible();
  });

  test('una venta ya facturada no se factura dos veces: la pantalla remite al comprobante', async () => {
    const page = ownerPage;
    await page.goto(`/members/${memberId}`);
    await page.getByRole('row', { name: /Membresía mensual/ }).getByRole('link', { name: 'Facturar' }).click();
    await expect(page.getByText('Esta venta ya tiene una factura electrónica.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Solo emitir (enviar después)' })).toHaveCount(0);
    await page.getByRole('link', { name: 'Ver la factura' }).click();
    await expect(page.getByRole('heading', { name: /^Factura 001-001-/, level: 1 })).toBeVisible();
  });

  test('un comprobante sin enviar se descarta con motivo y la venta se vuelve a emitir con otro número', async () => {
    const page = ownerPage;
    await page.goto('/invoicing');
    await page.getByRole('link', { name: /^001-001-/ }).first().click();
    const firstNumber = (await page.getByRole('heading', { level: 1 }).innerText()).replace('Factura ', '');

    await page.getByRole('button', { name: 'Descartar' }).click();
    const dialog = page.getByRole('dialog', { name: 'Descartar comprobante' });
    await dialog.getByLabel('Motivo').fill('corto');
    await expect(dialog.getByRole('button', { name: 'Descartar' })).toBeDisabled();
    await dialog.getByLabel('Motivo').fill('Se emitió con un dato equivocado');
    await dialog.getByRole('button', { name: 'Descartar' }).click();
    await expect(page.getByText('Comprobante descartado', { exact: true })).toBeVisible();
    await expect(page.getByText('Este comprobante fue descartado')).toBeVisible();
    await expect(page.getByText('Se emitió con un dato equivocado')).toBeVisible();

    await page.getByRole('link', { name: 'Volver a emitir esta venta' }).click();
    await expect(page.getByRole('heading', { name: 'Emitir factura electrónica', level: 1 })).toBeVisible();
    await page.getByRole('button', { name: 'Solo emitir (enviar después)' }).click();
    const secondNumber = (await page.getByRole('heading', { name: /^Factura 001-001-/, level: 1 }).innerText()).replace('Factura ', '');
    expect(secondNumber).not.toBe(firstNumber);

    // El listado muestra las dos: la vigente y la descartada.
    await page.goto('/invoicing');
    await expect(page.getByRole('row', { name: new RegExp(firstNumber) })).toContainText('Descartada');
    await expect(page.getByRole('row', { name: new RegExp(secondNumber) })).toContainText('Por firmar');
  });

  test('el listado filtra por estado y busca por número', async () => {
    const page = ownerPage;
    await page.goto('/invoicing');
    await page.getByRole('combobox', { name: 'Estado' }).selectOption('Discarded');
    await expect(page.getByRole('row', { name: /Descartada/ })).toHaveCount(1);
    await expect(page.getByRole('row', { name: /Por firmar/ })).toHaveCount(0);
    await page.getByRole('combobox', { name: 'Estado' }).selectOption('');
    await page.getByRole('searchbox').fill('Andrade');
    await expect(page.getByRole('row', { name: /Lucía Andrade/ })).toHaveCount(2);
  });
});
