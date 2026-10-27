import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { AccessDecision, Attendance as Visit } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { FcDate, FcDateTime, FcMoney } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';

interface Identity {
  memberId: null;
  memberCode: string | null;
  qrToken: string | null;
}

/** Asistencia: validación de acceso con la política comercial, entrada y salida, excepciones autorizadas y visitas. */
@Component({
  selector: 'fc-attendance',
  imports: [RouterLink, TranslocoPipe, Badge, Pager, ReasonDialog, FcDate, FcDateTime, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'attendance.title' | transloco }}</h1>
    </header>

    @if (canRegister()) {
      <section class="card section">
        <h2>{{ 'attendance.checkInTitle' | transloco }}</h2>
        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <div class="form-grid">
          <label class="field">
            <span>{{ 'attendance.branch' | transloco }}</span>
            <select [value]="branchId()" (change)="branchId.set($any($event.target).value); decision.set(null)">
              <option value="">{{ 'attendance.branchPlaceholder' | transloco }}</option>
              @for (branch of catalog.branches(); track branch.id) {
                <option [value]="branch.id" [selected]="branch.id === branchId()">{{ branch.name }}</option>
              }
            </select>
          </label>
          <label class="field">
            <span>{{ 'attendance.identifier' | transloco }}</span>
            <input
              autocomplete="off"
              autocapitalize="characters"
              spellcheck="false"
              [value]="identifier()"
              (input)="identifier.set($any($event.target).value); decision.set(null)"
              (keydown.enter)="validate(); $event.preventDefault()"
            />
            <small class="muted">{{ 'attendance.identifierHint' | transloco }}</small>
          </label>
        </div>
        <div class="inline-actions">
          <button type="button" class="btn" [disabled]="busy()" (click)="validate()">{{ 'attendance.validate' | transloco }}</button>
          <button type="button" class="btn" [disabled]="busy()" (click)="checkOut()">{{ 'attendance.checkOut' | transloco }}</button>
        </div>

        @if (decision(); as d) {
          <div class="decision" [class.decision-ok]="d.allowed" [class.decision-denied]="!d.allowed" role="status">
            <strong>{{ (d.allowed ? 'attendance.decisionAllowed' : 'attendance.decisionDenied') | transloco }}</strong>
            — {{ d.memberName }} <small class="muted">{{ d.memberCode }}</small>
            @if (d.message) {
              <p>{{ d.message }}</p>
            }
            <p class="muted">
              @if (d.planName) {
                {{ d.planName }}
              }
              @if (d.membershipEnd) {
                · {{ 'attendance.membershipEnds' | transloco: { date: (d.membershipEnd | fcDate) } }}
              }
              @if (d.owedAmount > 0) {
                · {{ 'attendance.owed' | transloco: { amount: (d.owedAmount | fcMoney) } }}
              }
            </p>
            @if (d.alreadyInside) {
              <p>{{ 'attendance.alreadyInside' | transloco }}</p>
            }
            <div class="inline-actions">
              @if (d.allowed && !d.alreadyInside) {
                <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="checkIn(null)">{{ 'attendance.checkIn' | transloco }}</button>
              }
              @if (!d.allowed && d.overridable && canOverride() && !d.alreadyInside) {
                <button type="button" class="btn" (click)="overriding.set(true)">{{ 'attendance.overrideAction' | transloco }}</button>
              }
              <a class="btn btn-ghost" [routerLink]="['/members', d.memberId]">{{ 'common.view' | transloco }}</a>
            </div>
          </div>
        }
      </section>
    }

    <section class="section">
      <h2>{{ 'attendance.inside' | transloco }} ({{ inside().length }})</h2>
      <div class="card table-card">
        <table class="table">
          <thead>
            <tr>
              <th>{{ 'attendance.member' | transloco }}</th>
              <th>{{ 'attendance.plan' | transloco }}</th>
              <th>{{ 'common.branch' | transloco }}</th>
              <th>{{ 'attendance.in' | transloco }}</th>
            </tr>
          </thead>
          <tbody>
            @for (v of inside(); track v.id) {
              <tr>
                <td><a [routerLink]="['/members', v.memberId]">{{ v.memberName }}</a> <small class="muted">{{ v.memberCode }}</small></td>
                <td>{{ v.planName ?? '—' }}</td>
                <td>{{ v.branchName }}</td>
                <td>{{ v.checkInAt | fcDateTime }} @if (v.isException) { <fc-badge tone="warn">{{ 'attendance.exception' | transloco }}</fc-badge> }</td>
              </tr>
            } @empty {
              <tr><td colspan="4" class="empty">{{ 'attendance.insideEmpty' | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>
    </section>

    <section class="section">
      <h2>{{ 'attendance.history' | transloco }}</h2>
      <div class="toolbar">
        <input type="search" class="search" [placeholder]="'common.search' | transloco" [attr.aria-label]="'common.search' | transloco" (input)="list.search($any($event.target).value)" />
        <select [attr.aria-label]="'common.branch' | transloco" (change)="list.set({ branchId: $any($event.target).value })">
          <option value="">{{ 'common.allBranches' | transloco }}</option>
          @for (branch of catalog.branches(); track branch.id) {
            <option [value]="branch.id">{{ branch.name }}</option>
          }
        </select>
        <label class="inline-field">
          <span>{{ 'common.from' | transloco }}</span>
          <input type="date" (change)="list.set({ from: $any($event.target).value })" />
        </label>
        <label class="inline-field">
          <span>{{ 'common.to' | transloco }}</span>
          <input type="date" (change)="list.set({ to: $any($event.target).value })" />
        </label>
        <label class="check">
          <input type="checkbox" (change)="list.set({ exception: $any($event.target).checked ? true : undefined })" />
          <span>{{ 'attendance.onlyExceptions' | transloco }}</span>
        </label>
      </div>
      @if (list.error()) {
        <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
      }
      <div class="card table-card">
        <table class="table">
          <thead>
            <tr>
              <th>{{ 'attendance.member' | transloco }}</th>
              <th>{{ 'common.branch' | transloco }}</th>
              <th>{{ 'attendance.in' | transloco }}</th>
              <th>{{ 'attendance.out' | transloco }}</th>
              <th>{{ 'attendance.duration' | transloco }}</th>
              <th>{{ 'attendance.method' | transloco }}</th>
              <th>{{ 'attendance.registeredBy' | transloco }}</th>
            </tr>
          </thead>
          <tbody>
            @for (v of list.items(); track v.id) {
              <tr>
                <td><a [routerLink]="['/members', v.memberId]">{{ v.memberName }}</a></td>
                <td>{{ v.branchName }}</td>
                <td>{{ v.checkInAt | fcDateTime }}</td>
                <td>{{ v.checkOutAt | fcDateTime }}</td>
                <td>{{ v.durationMinutes === null ? '—' : ('attendance.minutes' | transloco: { count: v.durationMinutes }) }}</td>
                <td>
                  {{ 'enums.attendanceMethod.' + v.method | transloco }}
                  @if (v.isException) {
                    <fc-badge tone="warn">{{ 'attendance.exception' | transloco }}</fc-badge>
                    @if (v.exceptionReason) {
                      <br /><small class="muted">{{ v.exceptionReason }}</small>
                    }
                  }
                </td>
                <td>{{ v.registeredByName ?? '—' }}</td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="empty">{{ (list.loading() ? 'common.loading' : 'attendance.historyEmpty') | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>
      <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
    </section>

    @if (overriding()) {
      <fc-reason-dialog
        [title]="'attendance.overrideTitle' | transloco"
        [label]="'attendance.overrideReason' | transloco"
        [hint]="'attendance.overrideHint' | transloco"
        [confirmLabel]="'attendance.overrideAction' | transloco"
        [busy]="busy()"
        [error]="overrideError()"
        (submitted)="checkIn($event)"
        (cancelled)="overriding.set(false)"
      />
    }
  `,
})
export class AttendancePage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly list = new ListState<Visit>((query) => this.api.attendance(query), this.errors, inject(DestroyRef));
  protected readonly inside = signal<Visit[]>([]);
  protected readonly canRegister = computed(() => this.session.has(Perm.AttendanceRegister));
  protected readonly canOverride = computed(() => this.session.has(Perm.AttendanceOverride));

  protected readonly branchId = signal('');
  protected readonly identifier = signal('');
  protected readonly decision = signal<AccessDecision | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly overriding = signal(false);
  protected readonly overrideError = signal<string | null>(null);

  constructor() {
    // Con una sola sucursal disponible se preselecciona (también cuando el catálogo llega después).
    effect(() => {
      const branches = this.catalog.branches();
      if (branches.length === 1 && !untracked(this.branchId)) {
        this.branchId.set(branches[0].id);
      }
    });
  }

  ngOnInit(): void {
    this.catalog.ensure();
    this.list.load();
    this.loadInside();
  }

  protected validate(): void {
    const identity = this.identity();
    if (!identity) {
      return;
    }
    this.run(() => {
      this.api.validateAccess({ branchId: this.branchId(), ...identity }).subscribe({
        next: (decision) => {
          this.busy.set(false);
          this.decision.set(decision);
        },
        error: (err: unknown) => this.fail(err),
      });
    });
  }

  /** `reason` null = entrada normal; con texto = excepción autorizada (el servidor exige el permiso y registra el motivo). */
  protected checkIn(reason: string | null): void {
    const identity = this.identity();
    if (!identity) {
      return;
    }
    this.run(() => {
      this.overrideError.set(null);
      this.api.checkIn({ branchId: this.branchId(), ...identity, overrideReason: reason }).subscribe({
        next: (visit) => {
          this.busy.set(false);
          this.overriding.set(false);
          this.toast.success('attendance.checkedIn', { name: visit.memberName });
          this.reset();
        },
        error: (err: unknown) => {
          this.busy.set(false);
          const message = this.errors.message(err);
          if (this.overriding()) {
            this.overrideError.set(message);
          } else {
            this.formError.set(message);
          }
        },
      });
    });
  }

  protected checkOut(): void {
    const identity = this.identity();
    if (!identity) {
      return;
    }
    this.run(() => {
      this.api.checkOut(identity).subscribe({
        next: (visit) => {
          this.busy.set(false);
          this.toast.success('attendance.checkedOut', { name: visit.memberName });
          this.reset();
        },
        error: (err: unknown) => this.fail(err),
      });
    });
  }

  private identity(): Identity | null {
    const text = this.identifier().trim();
    if (!text) {
      this.formError.set(this.errors.text('attendance.needIdentifier'));
      return null;
    }
    if (!this.branchId()) {
      this.formError.set(this.errors.text('attendance.needBranch'));
      return null;
    }
    const isQr = text.toLowerCase().startsWith('fc1_');
    return { memberId: null, memberCode: isQr ? null : text.toUpperCase(), qrToken: isQr ? text : null };
  }

  private run(action: () => void): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.formError.set(null);
    action();
  }

  private fail(err: unknown): void {
    this.busy.set(false);
    this.formError.set(this.errors.message(err));
  }

  private reset(): void {
    this.identifier.set('');
    this.decision.set(null);
    this.list.load();
    this.loadInside();
  }

  private loadInside(): void {
    this.api.attendance({ open: true, pageSize: 100 }).subscribe({ next: (page) => this.inside.set(page.items), error: () => this.inside.set([]) });
  }
}
