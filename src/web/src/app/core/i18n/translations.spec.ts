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
  'classes.not_found', 'classes.name_taken', 'classes.branch_required', 'classes.branch_immutable', 'classes.instructor_invalid',
  'classes.instructor_not_in_branch', 'classes.booking_window_incoherent', 'classes.waitlist_capacity_required',
  'classes.waitlist_disabled_with_capacity', 'classes.payment_policy_needs_membership', 'classes.session_not_found',
  'classes.series_not_found', 'classes.class_inactive', 'classes.session_in_past', 'classes.session_not_modifiable',
  'classes.instructor_overlap', 'classes.instructor_unavailable', 'classes.session_nothing_to_change', 'classes.series_cancelled',
  'classes.series_range_invalid', 'classes.series_in_past', 'classes.series_cannot_shrink', 'classes.series_too_many_sessions',
  'classes.series_no_sessions', 'classes.capacity_below_booked', 'classes.session_changed', 'classes.reservation_not_found',
  'classes.member_not_found', 'classes.already_booked', 'classes.session_full', 'classes.waitlist_disabled', 'classes.waitlist_full',
  'classes.reservation_not_offered', 'classes.offer_expired', 'classes.session_not_bookable', 'classes.booking_not_open',
  'classes.booking_closed', 'classes.member_schedule_conflict', 'classes.cancellation_deadline_passed',
  'classes.late_cancellation_reason_required', 'classes.session_started', 'classes.reservation_not_cancellable',
  'classes.reservation_changed', 'classes.member_inactive', 'classes.no_active_membership', 'classes.membership_frozen',
  'classes.membership_pending_payment', 'classes.plan_branch_restricted', 'classes.debt_pending', 'classes.debt_overdue',
  'classes.minimum_payment_not_met', 'classes.below_min_age', 'classes.member_age_unknown', 'classes.session_not_started',
  'classes.session_cancelled', 'classes.reservation_not_in_session', 'classes.reservation_not_attendable',
  'classes.attendance_duplicate_entries', 'classes.notification_not_found', 'classes.notification_required',
  'classes.notification_duplicate_types',
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
  'taxes.rate_in_past', 'taxes.rate_overlap', 'members.whatsapp_phone_required', 'members.access_exists', 'exercises.not_found', 'exercises.name_taken', 'exercises.global_readonly', 'trainers.not_found', 'trainers.no_profile', 'trainers.already_exists', 'trainers.user_not_found', 'trainers.user_inactive', 'trainers.inactive', 'trainers.has_active_members', 'trainers.member_not_found', 'trainers.member_inactive', 'trainers.already_assigned', 'trainers.no_active_assignment', 'trainers.branch_mismatch', 'trainers.assignment_conflict', 'trainers.not_yours', 'trainers.not_assigned', 'trainers.availability_branch_invalid', 'trainers.availability_too_many', 'trainers.availability_invalid_range', 'trainers.availability_overlap', 'members.access_missing', 'members.access_email_required', 'members.access_member_inactive', 'members.access_conflict', 'members.access_role_missing', 'portal.no_member_linked',
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
