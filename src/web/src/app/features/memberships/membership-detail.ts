import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { Observable, switchMap } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { Member, Membership, MembershipEvent, Plan } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, membershipTone, obligationTone } from '../../shared/badge';
import { Confirm } from '../../shared/confirm';
import { FcDate, FcDateTime, FcMoney } from '../../shared/format.pipes';
import { Modal } from '../../shared/modal';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';
import { PaymentDialog } from '../billing/payment-dialog';

type Dialog = 'renew' | 'freeze' | 'reactivate' | 'cancel' | 'pay' | null;

/** Detalle de una membresía: términos contratados (copia al inscribir), obligación de pago, historial y acciones. */
@Component({
  selector: 'fc-membership-detail',
  imports: [RouterLink, TranslocoPipe, Badge, Confirm, Modal, ReasonDialog, PaymentDialog, FcDate, FcDateTime, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error() && !membership()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
      <a routerLink="/memberships" class="btn">{{ 'common.back' | transloco }}</a>
    } @else if (membership(); as m) {
      <header class="page-header">
        <div>
          <h1>{{ m.planName }}</h1>
          <p class="muted">
            <a [routerLink]="['/members', m.memberId]">{{ m.memberName }}</a> · {{ m.memberCode }}
            <fc-badge [tone]="tone(m.status)">{{ 'enums.membershipStatus.' + m.status | transloco }}</fc-badge>
          </p>
        </div>
      </header>

      @if (error()) {
        <p class="alert alert-danger" role="alert">{{ error() }}</p>
      }

      <div class="inline-actions">
        @if (can(perm.PaymentsCreate) && m.obligation && m.obligation.balance > 0 && m.obligation.status !== 'Voided') {
          <button type="button" class="btn btn-primary" (click)="dialog.set('pay')">{{ 'memberships.payNow' | transloco }}</button>
        }
        @if (can(perm.MembershipsRenew) && (m.status === 'Active' || m.status === 'Expired')) {
          <button type="button" class="btn" (click)="startRenew()">{{ 'memberships.renew' | transloco }}</button>
        }
        @if (can(perm.MembershipsFreeze) && m.status === 'Active' && m.allowFreeze && m.remainingFreezeDays > 0) {
          <button type="button" class="btn" (click)="dialog.set('freeze')">{{ 'memberships.freeze' | transloco }}</button>
        }
        @if (can(perm.MembershipsFreeze) && m.status === 'Frozen') {
          <button type="button" class="btn" (click)="dialog.set('reactivate')">{{ 'memberships.reactivate' | transloco }}</button>
        }
        @if (can(perm.MembershipsCancel) && m.status !== 'Cancelled' && m.status !== 'Expired') {
          <button type="button" class="btn btn-danger" (click)="dialog.set('cancel')">{{ 'memberships.cancel' | transloco }}</button>
        }
      </div>

      <div class="grid-2">
        <section class="card section">
          <h2>{{ 'memberships.detail' | transloco }}</h2>
          <dl class="kv">
            <dt>{{ 'memberships.start' | transloco }}</dt>
            <dd>{{ m.startDate | fcDate }}</dd>
            <dt>{{ 'memberships.end' | transloco }}</dt>
            <dd>{{ m.endDate | fcDate }}</dd>
            <dt>{{ 'memberships.listPrice' | transloco }}</dt>
            <dd>{{ m.planPrice | fcMoney }}</dd>
            @if (m.discountAmount > 0) {
              <dt>{{ 'memberships.discount' | transloco }}</dt>
              <dd>{{ m.discountAmount | fcMoney }} @if (m.promotionName) { · {{ m.promotionName }} }</dd>
            }
            <dt>{{ 'memberships.price' | transloco }}</dt>
            <dd><strong>{{ m.agreedPrice | fcMoney }}</strong></dd>
            <dt>{{ 'memberships.tax' | transloco }}</dt>
            <dd>{{ 'enums.taxKind.' + m.taxKind | transloco }} @if (m.taxKind === 'General') { ({{ m.taxPercent }} %) }</dd>
            <dt>{{ 'memberships.freezeAllowed' | transloco }}</dt>
            <dd>
              {{ (m.allowFreeze ? 'common.yes' : 'common.no') | transloco }}
              @if (m.allowFreeze) {
                · {{ 'memberships.freezeUsed' | transloco: { used: m.frozenDaysUsed, max: m.maxFreezeDays } }}
              }
            </dd>
            @if (m.freezeStartedOn) {
              <dt>{{ 'enums.membershipStatus.Frozen' | transloco }}</dt>
              <dd>{{ 'memberships.frozenSince' | transloco: { date: (m.freezeStartedOn | fcDate) } }}</dd>
            }
            @if (m.renewedFromId) {
              <dt></dt>
              <dd><a [routerLink]="['/memberships', m.renewedFromId]">{{ 'memberships.renewedFrom' | transloco }}</a></dd>
            }
            @if (m.cancelReason) {
              <dt>{{ 'memberships.cancelReason' | transloco }}</dt>
              <dd>{{ m.cancelReason }}</dd>
            }
          </dl>
          <h3>{{ 'memberships.terms' | transloco }}</h3>
          <p class="muted">{{ 'memberships.termsNote' | transloco }}</p>
          <dl class="kv">
            <dt>{{ 'plans.duration' | transloco }}</dt>
            <dd>{{ m.durationValue }} {{ 'enums.durationUnit.' + m.durationUnit | transloco }}</dd>
            <dt>{{ 'plans.access' | transloco }}</dt>
            <dd>{{ m.allowedAccess ?? '—' }}</dd>
            <dt>{{ 'plans.terms' | transloco }}</dt>
            <dd>{{ m.commercialTerms ?? '—' }}</dd>
          </dl>
        </section>

        <section class="card section">
          <h2>{{ 'memberships.obligation' | transloco }}</h2>
          @if (m.obligation; as o) {
            <dl class="kv">
              <dt>{{ 'billing.reference' | transloco }}</dt>
              <dd>{{ o.reference }}</dd>
              <dt>{{ 'billing.dueDate' | transloco }}</dt>
              <dd>{{ o.dueDate | fcDate }}</dd>
              <dt>{{ 'billing.payable' | transloco }}</dt>
              <dd>{{ o.payableAmount | fcMoney }}</dd>
              <dt>{{ 'billing.paid' | transloco }}</dt>
              <dd>{{ o.paidAmount | fcMoney }}</dd>
              @if (o.reservedAmount > 0) {
                <dt>{{ 'billing.reserved' | transloco }}</dt>
                <dd>{{ o.reservedAmount | fcMoney }}</dd>
              }
              <dt>{{ 'billing.balance' | transloco }}</dt>
              <dd><strong>{{ o.balance | fcMoney }}</strong></dd>
              <dt>{{ 'common.status' | transloco }}</dt>
              <dd><fc-badge [tone]="obTone(o.status)">{{ 'enums.obligationStatus.' + o.status | transloco }}</fc-badge></dd>
            </dl>
          }
        </section>
      </div>

      <section class="section">
        <h2>{{ 'memberships.history' | transloco }}</h2>
        <ol class="timeline">
          @for (e of events(); track e.id) {
            <li>
              <strong>{{ 'enums.membershipEvent.' + e.type | transloco }}</strong>
              <span class="muted"> · {{ e.occurredAt | fcDateTime }}</span>
              @if (detailText(e); as text) {
                <div>{{ text }}</div>
              }
            </li>
          } @empty {
            <li class="muted">{{ 'memberships.noEvents' | transloco }}</li>
          }
        </ol>
      </section>

      @switch (dialog()) {
        @case ('pay') {
          <fc-payment-dialog [member]="payMember()" [obligationId]="m.obligation?.id ?? null" (closed)="dialog.set(null)" (done)="onPaid()" />
        }
        @case ('freeze') {
          <fc-reason-dialog
            [title]="'memberships.freeze' | transloco"
            [label]="'memberships.freezeReason' | transloco"
            [confirmLabel]="'memberships.freeze' | transloco"
            [minLength]="0"
            [busy]="busy()"
            [error]="dialogError()"
            (submitted)="run(api.freeze(m.id, $event || null), 'memberships.frozenToast')"
            (cancelled)="dialog.set(null)"
          />
        }
        @case ('cancel') {
          <fc-reason-dialog
            [title]="'memberships.cancel' | transloco"
            [label]="'memberships.cancelReason' | transloco"
            [hint]="'memberships.cancelHint' | transloco"
            [confirmLabel]="'memberships.cancelConfirm' | transloco"
            [danger]="true"
            [busy]="busy()"
            [error]="dialogError()"
            (submitted)="run(api.cancelMembership(m.id, $event), 'memberships.cancelledToast')"
            (cancelled)="dialog.set(null)"
          />
        }
        @case ('reactivate') {
          <fc-confirm
            [title]="'memberships.reactivate' | transloco"
            [message]="'memberships.reactivateConfirm' | transloco"
            [confirmLabel]="'memberships.reactivate' | transloco"
            (confirmed)="run(api.reactivate(m.id), 'memberships.reactivatedToast')"
            (cancelled)="dialog.set(null)"
          />
        }
        @case ('renew') {
          <fc-modal [title]="'memberships.renew' | transloco" (closed)="dialog.set(null)">
            <p class="muted">{{ 'memberships.renewHint' | transloco }}</p>
            @if (dialogError()) {
              <p class="alert alert-danger" role="alert">{{ dialogError() }}</p>
            }
            <label class="field">
              <span>{{ 'memberships.plan' | transloco }}</span>
              <select (change)="renewPlan.set($any($event.target).value)">
                <option value="">{{ m.planName }}</option>
                @for (plan of plans(); track plan.id) {
                  @if (plan.id !== m.planId) {
                    <option [value]="plan.id">{{ plan.name }}</option>
                  }
                }
              </select>
            </label>
            @if (can(perm.PromotionsApply)) {
              <label class="field">
                <span>{{ 'memberships.promotionCode' | transloco }}</span>
                <input maxlength="30" autocapitalize="characters" (input)="renewCode.set($any($event.target).value)" />
              </label>
            }
            <label class="field">
              <span>{{ 'memberships.dueDate' | transloco }}</span>
              <input type="date" (input)="renewDue.set($any($event.target).value)" />
              <small class="muted">{{ 'memberships.dueDateHint' | transloco }}</small>
            </label>
            <ng-container footer>
              <button type="button" class="btn" (click)="dialog.set(null)">{{ 'common.cancel' | transloco }}</button>
              <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="submitRenew(m)">{{ 'memberships.renew' | transloco }}</button>
            </ng-container>
          </fc-modal>
        }
      }
    } @else {
      <p class="muted">{{ 'common.loading' | transloco }}</p>
    }
  `,
})
export class MembershipDetail implements OnInit {
  protected readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly perm = Perm;
  protected readonly membership = signal<Membership | null>(null);
  protected readonly events = signal<MembershipEvent[]>([]);
  protected readonly plans = signal<Plan[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly dialog = signal<Dialog>(null);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly renewPlan = signal('');
  protected readonly renewCode = signal('');
  protected readonly renewDue = signal('');
  protected readonly tone = membershipTone;
  protected readonly obTone = obligationTone;

  /** Alumno mínimo que el diálogo de pago necesita para precargar sus obligaciones. */
  protected readonly payMember = computed<Member | null>(() => {
    const m = this.membership();
    return m ? ({ id: m.memberId, code: m.memberCode, firstName: m.memberName, lastName: '', branchId: m.branchId } as Member) : null;
  });

  ngOnInit(): void {
    this.route.paramMap
      .pipe(
        switchMap((params) => this.api.membership(params.get('id') ?? '')),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (membership) => {
          this.membership.set(membership);
          this.loadEvents(membership.id);
        },
        error: (err: unknown) => this.error.set(this.errors.message(err)),
      });
  }

  protected can(permission: string): boolean {
    return this.session.has(permission);
  }

  protected startRenew(): void {
    this.dialogError.set(null);
    this.renewPlan.set('');
    this.renewCode.set('');
    this.renewDue.set('');
    if (this.plans().length === 0) {
      this.api.plans({ isActive: true, pageSize: 100 }).subscribe((page) => this.plans.set(page.items));
    }
    this.dialog.set('renew');
  }

  protected submitRenew(m: Membership): void {
    this.run(
      this.api.renew(m.id, {
        planId: this.renewPlan() || null,
        promotionId: null,
        promotionCode: this.renewCode().trim() || null,
        dueDate: this.renewDue() || null,
      }),
      'memberships.renewedToast',
      true,
    );
  }

  protected onPaid(): void {
    this.dialog.set(null);
    const m = this.membership();
    if (m) {
      this.refresh(m.id);
    }
  }

  /** Ejecuta una operación sobre la membresía; el aviso de éxito solo aparece cuando el servidor la confirmó. */
  protected run(request: Observable<Membership>, toastKey: string, navigateToResult = false): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.dialogError.set(null);
    request.subscribe({
      next: (result) => {
        this.busy.set(false);
        this.dialog.set(null);
        this.toast.success(toastKey);
        if (navigateToResult && result.id !== this.membership()?.id) {
          void this.router.navigate(['/memberships', result.id]);
        } else {
          this.refresh(result.id);
        }
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.dialogError.set(this.errors.message(err));
      },
    });
  }

  protected detailText(event: MembershipEvent): string {
    if (!event.details) {
      return '';
    }
    try {
      const data = JSON.parse(event.details) as { reason?: string; grantedDays?: number };
      const parts: string[] = [];
      if (data.grantedDays !== undefined) {
        parts.push(this.errors.text('memberships.event.grantedDays', { days: data.grantedDays }));
      }
      if (data.reason) {
        parts.push(data.reason);
      }
      return parts.join(' · ');
    } catch {
      return '';
    }
  }

  private refresh(id: string): void {
    this.api.membership(id).subscribe({
      next: (membership) => {
        this.membership.set(membership);
        this.loadEvents(id);
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  private loadEvents(id: string): void {
    this.api.membershipEvents(id).subscribe({ next: (events) => this.events.set(events), error: () => this.events.set([]) });
  }
}
