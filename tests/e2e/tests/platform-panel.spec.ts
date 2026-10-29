import { expect, test } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

test.describe('Panel SaaS de plataforma', () => {
  test.beforeEach(async ({ page }) => {
    const { email, password } = platformCredentials();
    await signIn(page, 'platform', email, password);
    await expect(nav(page)).toBeVisible();
  });

  test('el equipo de plataforma recorre métricas, planes, organizaciones y cobros', async ({ page }) => {
    await nav(page).getByRole('link', { name: 'Métricas SaaS', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Métricas del producto SaaS', level: 1 })).toBeVisible();
    await expect(page.getByText('MRR (contratado por mes)').first()).toBeVisible();

    await nav(page).getByRole('link', { name: 'Planes comerciales', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Planes comerciales', level: 1 })).toBeVisible();
    await expect(page.getByText('legacy').first()).toBeVisible();

    await nav(page).getByRole('link', { name: 'Cobros comerciales', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Cobros comerciales', level: 1 })).toBeVisible();

    // Sin secreto configurado no hay proveedor: se dice claramente y no se simula ninguno.
    await page.getByRole('tab', { name: 'Proveedor de pagos' }).click();
    await expect(page.getByRole('heading', { name: 'Integración con el proveedor de pagos' })).toBeVisible();
    await expect(page.getByText('Sin proveedor')).toBeVisible();

    await nav(page).getByRole('link', { name: 'Organizaciones', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Organizaciones clientes', level: 1 })).toBeVisible();
    await page.getByRole('link', { name: 'Abrir' }).first().click();
    await expect(page).toHaveURL(/\/platform\/organizations\/[0-9a-f-]{36}$/);
    await expect(page.getByRole('heading', { name: 'Notas administrativas' })).toBeVisible();
  });
});
