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
  'attendance.already_checked_in', 'attendance.invalid_qr', 'attendance.no_open_entry', 'attendance.not_found',
  'attendance.open_entry_elsewhere', 'attendance.override_not_allowed', 'attendance.member_inactive', 'attendance.membership_expired',
  'attendance.membership_frozen', 'attendance.membership_not_started', 'attendance.membership_pending_payment',
  'attendance.no_active_membership', 'attendance.plan_branch_restricted', 'attendance.debt_pending', 'attendance.debt_overdue',
  'attendance.minimum_payment_not_met', 'branches.inactive',
  'cash.close_not_allowed', 'cash.difference_requires_notes', 'cash.has_open_sessions', 'cash.insufficient_funds', 'cash.no_open_session',
  'cash.register_already_open', 'cash.register_inactive', 'cash.register_name_taken', 'cash.register_not_found', 'cash.session_closed',
  'cash.session_not_found', 'cash.user_has_open_session', 'idempotency.key_required', 'idempotency.key_reused',
  'members.duplicate_identification', 'members.inactive', 'members.not_found',
  'memberships.already_cancelled', 'memberships.already_renewed', 'memberships.cannot_renew', 'memberships.expired',
  'memberships.freeze_blocked_by_renewal', 'memberships.freeze_limit_reached', 'memberships.freeze_not_allowed', 'memberships.frozen',
  'memberships.not_active', 'memberships.not_found', 'memberships.not_frozen', 'memberships.overlap', 'memberships.pending_payment',
  'memberships.start_in_past', 'memberships.start_too_far', 'memberships.zero_amount',
  'obligations.already_voided', 'obligations.due_in_past', 'obligations.has_payments', 'obligations.not_found',
  'payments.allocation_mismatch', 'payments.already_reversed', 'payments.cash_requires_open_cash_session', 'payments.exceeds_balance',
  'payments.not_found', 'payments.not_pending', 'payments.nothing_to_pay', 'payments.obligation_not_payable', 'payments.paid_at_invalid',
  'payments.reversal_requires_open_cash_session',
  'plans.branches_required', 'plans.name_taken', 'plans.not_found', 'plans.not_sellable', 'plans.scope_not_allowed',
  'promotions.apply_not_allowed', 'promotions.code_taken', 'promotions.discount_above_maximum', 'promotions.exhausted',
  'promotions.max_uses_below_used', 'promotions.not_applicable', 'promotions.not_available', 'promotions.not_found',
  'taxes.rate_in_past', 'taxes.rate_overlap', 'members.whatsapp_phone_required',
  'validation.failed', 'request.body_required', 'request.invalid_body','conflict.duplicate', 'conflict.concurrency', 'server.internal_error',
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
