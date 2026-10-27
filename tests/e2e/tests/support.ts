import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, type Page } from '@playwright/test';

/** Credenciales del administrador de plataforma: variables E2E_* o, en local, el .env de docker compose (nunca versionado). */
export function platformCredentials(): { email: string; password: string } {
  const email = process.env['E2E_PLATFORM_EMAIL'];
  const password = process.env['E2E_PLATFORM_PASSWORD'];
  if (email && password) {
    return { email, password };
  }

  const file = resolve(__dirname, '../../../infra/compose/.env');
  const values = Object.fromEntries(
    readFileSync(file, 'utf8')
      .split(/\r?\n/)
      .filter((line) => line.includes('=') && !line.startsWith('#'))
      .map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]),
  );
  return { email: values['PLATFORM_ADMIN_EMAIL'], password: values['PLATFORM_ADMIN_PASSWORD'] };
}

export async function signIn(page: Page, tenant: string, email: string, password: string): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Gimnasio').fill(tenant);
  await page.getByLabel('Correo electrónico').fill(email);
  await page.getByLabel('Contraseña').fill(password);
  await page.getByRole('button', { name: 'Iniciar sesión' }).click();
}

export async function signOut(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Cerrar sesión' }).click();
  await expect(page).toHaveURL(/\/login/);
}

export const nav = (page: Page) => page.getByRole('navigation', { name: 'Menú principal' });
