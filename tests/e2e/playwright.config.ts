import { defineConfig } from '@playwright/test';

/**
 * Pruebas de extremo a extremo contra el entorno real: API + PostgreSQL (docker compose) y el frontend.
 * Por defecto levanta `ng serve` (puerto 4300, con proxy a la API en :8080); con E2E_BASE_URL usa un entorno ya desplegado.
 */
export default defineConfig({
  testDir: './tests',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:4300',
    locale: 'es-EC',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: process.env.E2E_BASE_URL
    ? undefined
    : {
        command: 'npm --prefix ../../src/web start',
        url: 'http://localhost:4300',
        reuseExistingServer: true,
        timeout: 180_000,
      },
});
