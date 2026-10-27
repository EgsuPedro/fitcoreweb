import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Translation, TranslocoLoader, TranslocoService, provideTransloco } from '@jsverse/transloco';
import { firstValueFrom, of } from 'rxjs';
import en from '../../../../public/i18n/en.json';
import es from '../../../../public/i18n/es.json';
import { ErrorMessages } from './messages';

type Tree = { [key: string]: string | Tree };

function keysOf(tree: Tree, prefix = ''): string[] {
  return Object.entries(tree).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : keysOf(value, `${prefix}${key}.`),
  );
}

// Códigos de error estables que la API puede devolver (FitCore.Platform.Application y FitCore.Api).
const API_ERROR_CODES = [
  'auth.invalid_credentials', 'auth.locked_out', 'auth.invalid_refresh', 'auth.refresh_reused', 'auth.unauthenticated',
  'auth.forbidden', 'auth.all_branches_required', 'auth.password_change_failed',
  'branches.not_found', 'branches.not_allowed', 'branches.code_taken', 'branches.invalid_header',
  'users.not_found', 'users.role_not_found', 'users.branch_not_found', 'users.role_not_assignable', 'users.cannot_edit_self',
  'users.email_taken', 'users.password_invalid', 'users.invalid',
  'roles.not_found', 'roles.name_taken', 'roles.in_use', 'roles.system_protected', 'roles.owner_protected',
  'roles.permission_not_found', 'roles.permission_not_grantable', 'roles.not_manageable', 'roles.all_branches_not_grantable',
  'tenants.slug_taken', 'tenants.not_found', 'tenants.platform_protected',
  'validation.failed', 'request.body_required', 'conflict.duplicate', 'conflict.concurrency', 'server.internal_error',
];

describe('traducciones', () => {
  it('español e inglés tienen exactamente las mismas claves', () => {
    expect(keysOf(en as Tree).sort()).toEqual(keysOf(es as Tree).sort());
  });

  it('ningún texto queda vacío', () => {
    for (const lang of [es, en] as Tree[]) {
      expect(keysOf(lang).filter((key) => key.length === 0)).toEqual([]);
    }
    const empty = JSON.stringify(es).includes('""') || JSON.stringify(en).includes('""');
    expect(empty).toBe(false);
  });

  it('todos los códigos de error de la API están traducidos en ambos idiomas', () => {
    for (const lang of [es, en] as Tree[]) {
      const known = new Set(keysOf(lang).map((key) => key.replace(/^errors\./, '')));
      expect(API_ERROR_CODES.filter((code) => !known.has(code))).toEqual([]);
    }
  });
});

class InMemoryLoader implements TranslocoLoader {
  getTranslation(lang: string) {
    return of((lang === 'en' ? en : es) as Translation);
  }
}

describe('ErrorMessages', () => {
  let messages: ErrorMessages;
  let transloco: TranslocoService;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideTransloco({
          config: { availableLangs: ['es', 'en'], defaultLang: 'es', prodMode: true },
          loader: InMemoryLoader,
        }),
      ],
    });
    transloco = TestBed.inject(TranslocoService);
    await firstValueFrom(transloco.load('es'));
    await firstValueFrom(transloco.load('en'));
    messages = TestBed.inject(ErrorMessages);
  });

  const http = (status: number, body: unknown) => new HttpErrorResponse({ status, error: body });

  it('traduce el código estable de la API al idioma activo', () => {
    expect(messages.message(http(401, { code: 'auth.invalid_credentials' }))).toBe('Gimnasio, correo o contraseña incorrectos.');

    transloco.setActiveLang('en');
    expect(messages.message(http(401, { code: 'auth.invalid_credentials' }))).toBe('Wrong gym, email or password.');
  });

  it('usa los mensajes de validación del servidor cuando no hay código traducible', () => {
    const error = http(422, { code: 'codigo.desconocido', errors: { name: ['El nombre es obligatorio.'] } });

    expect(messages.message(error)).toBe('El nombre es obligatorio.');
  });

  it('cae al mensaje por estado HTTP y luego al genérico', () => {
    expect(messages.message(http(403, null))).toBe('No tiene permiso para realizar esta acción.');
    expect(messages.message(http(0, null))).toContain('No se pudo conectar');
    expect(messages.message(http(418, null))).toBe('Ocurrió un error inesperado. Intente de nuevo.');
    expect(messages.message(new Error('x'))).toBe('Ocurrió un error inesperado. Intente de nuevo.');
  });
});
