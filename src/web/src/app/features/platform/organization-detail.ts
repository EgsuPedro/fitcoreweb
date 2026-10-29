import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { CommercialPlanSummary, InvoiceSummary, OrganizationActivity, OrganizationDetail, OrganizationNote } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDate, FcDateTime, FcMoney } from '../../shared/format.pipes';
import { AttemptKey } from '../../shared/idempotency';
import { Modal } from '../../shared/modal';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';
import { daysBetween, invoiceTone, isFree, parseAmount, subscriptionTone } from './platform-math';

type Action = 'assign' | 'activate' | 'renew' | 'reactivate' | 'resume-cancellation' | 'suspend' | 'cancel' | 'extend' | 'change-plan';

const METHODS = ['BankTransfer', 'Cash', 'Other'] as const;
const NEEDS_PLAN: readonly Action[] = ['assign', 'change-plan'];
const NEEDS_REASON: readonly Action[] = ['suspend', 'cancel', 'extend'];

/**
 * Ficha de una organización cliente para el equipo de plataforma: contrato, derechos y consumo agregado, cobros, notas internas y actividad
 * administrativa. Las acciones se piden al servidor, que valida permisos, transiciones y concurrencia; esta pantalla solo ofrece las que tienen sentido.
 * No muestra ni puede mostrar datos de los alumnos del gimnasio.
 */
@Component({
  selector: 'fc-platform-organization',
  imports: [RouterLink, TranslocoPipe, Badge, FcDate, FcDateTime, FcMoney, Modal, ReasonDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }
    @if (!org() && !error()) { <p class="muted" role="status">{{ 'common.loading' | transloco }}</p> }

    @if (org(); as o) {
      <header class="page-header">
        <div>
          <p><a [routerLink]="['/platform/organizations']">← {{ 'platform.organizations.title' | transloco }}</a></p>
          <h1>{{ o.name }}</h1>
          <p class="muted">{{ o.slug }} · {{ o.timeZone }} · {{ o.createdAt | fcDate }}</p>
        </div>
        <div>
          <fc-badge [tone]="o.tenantStatus === 'Active' ? 'ok' : 'danger'">{{ 'platform.tenantStatus.' + o.tenantStatus | transloco }}</fc-badge>
          @if (o.subscription; as s) { <fc-badge [tone]="tone(s.status)">{{ 'platform.status.' + s.status | transloco }}</fc-badge> }
        </div>
      </header>
      @if (o.tenantStatus !== 'Active') {
        <p class="alert alert-info">{{ 'platform.detail.adminSuspendedNote' | transloco }}</p>
      }

      <section class="card" aria-labelledby="sub-title">
        <h2 id="sub-title">{{ 'platform.detail.contract' | transloco }}</h2>
        @if (o.subscription; as s) {
          <dl class="summary">
            <div><dt>{{ 'platform.plans.plan' | transloco }}</dt><dd>{{ s.planName }} <small class="muted">v{{ s.planVersion }}</small></dd></div>
            <div><dt>{{ 'platform.plans.price' | transloco }}</dt><dd>{{ s.price | fcMoney }} · {{ 'platform.period.' + s.billingPeriod | transloco }}</dd></div>
            <div><dt>{{ 'platform.detail.currentPeriod' | transloco }}</dt><dd>{{ s.currentPeriodStart | fcDate }} → {{ s.currentPeriodEnd | fcDate }}</dd></div>
            <div><dt>{{ 'platform.organizations.nextDue' | transloco }}</dt><dd>{{ s.nextDueOn | fcDate }} @if (daysToDue(s.nextDueOn) !== null) { <small class="muted">({{ 'platform.detail.inDays' | transloco: { n: daysToDue(s.nextDueOn) } }})</small> }</dd></div>
            @if (s.trialEndsOn) { <div><dt>{{ 'platform.detail.trialEnds' | transloco }}</dt><dd>{{ s.trialEndsOn | fcDate }}</dd></div> }
            @if (s.graceEndsOn) { <div><dt>{{ 'platform.detail.graceEnds' | transloco }}</dt><dd>{{ s.graceEndsOn | fcDate }}</dd></div> }
            @if (s.cancelAtPeriodEnd) { <div><dt>{{ 'platform.detail.cancelScheduled' | transloco }}</dt><dd>{{ s.scheduledCancelReason }}</dd></div> }
            @if (s.pendingPlanId) { <div><dt>{{ 'platform.detail.pendingPlan' | transloco }}</dt><dd>{{ 'platform.detail.pendingPlanHint' | transloco }}</dd></div> }
            @if (s.suspensionReason) { <div><dt>{{ 'platform.detail.suspendedBecause' | transloco }}</dt><dd>{{ s.suspensionReason }}</dd></div> }
            @if (s.cancelReason) { <div><dt>{{ 'platform.detail.cancelledBecause' | transloco }}</dt><dd>{{ s.cancelReason }}</dd></div> }
          </dl>
        } @else {
          <p class="muted">{{ 'platform.detail.noSubscription' | transloco }}</p>
        }

        @if (canManage()) {
          <div class="row-actions">
            @for (a of available(); track a) {
              <button type="button" class="btn" [class.btn-danger]="a === 'cancel' || a === 'suspend'" (click)="openAction(a)">{{ 'platform.action.' + a | transloco }}</button>
            }
            @if (canIssue()) {
              <button type="button" class="btn" [disabled]="busy()" (click)="issue()">{{ 'platform.action.issue' | transloco }}</button>
            }
          </div>
        }
      </section>

      <section class="card" aria-labelledby="usage-title">
        <header class="section-header">
          <h2 id="usage-title">{{ 'platform.detail.usage' | transloco }}</h2>
          @if (canManage()) { <button type="button" class="btn" [disabled]="busy()" (click)="refreshUsage()">{{ 'platform.detail.refreshUsage' | transloco }}</button> }
        </header>
        @if (o.entitlement; as e) {
          <p>{{ 'platform.detail.accessMode' | transloco }}: <fc-badge [tone]="e.accessMode === 'Restricted' ? 'danger' : e.accessMode === 'Grace' ? 'warn' : 'ok'">{{ 'platform.accessMode.' + e.accessMode | transloco }}</fc-badge></p>
          <table class="table">
            <caption class="sr-only">{{ 'platform.detail.usage' | transloco }}</caption>
            <thead><tr><th scope="col">{{ 'platform.detail.resource' | transloco }}</th><th scope="col" class="num">{{ 'platform.detail.used' | transloco }}</th><th scope="col" class="num">{{ 'platform.detail.limit' | transloco }}</th></tr></thead>
            <tbody>
              @for (l of e.limits; track l.code) {
                <tr>
                  <th scope="row">{{ 'platform.limit.' + l.code | transloco }}</th>
                  <td class="num">{{ used(o, l.code) }}</td>
                  <td class="num">{{ l.limit === null ? ('platform.plans.unlimited' | transloco) : l.limit }}</td>
                </tr>
              }
            </tbody>
          </table>
          @if (o.usage; as u) { <p class="muted">{{ 'platform.detail.measuredAt' | transloco }}: {{ u.measuredAt | fcDateTime }}. {{ 'platform.organizations.usageNote' | transloco }}</p> }
          @else { <p class="muted">{{ 'platform.detail.notMeasured' | transloco }}</p> }
          <p><strong>{{ 'platform.detail.features' | transloco }}:</strong>
            @for (f of e.features; track f; let last = $last) { {{ 'platform.feature.' + f | transloco }}@if (!last) {, } }
          </p>
        } @else {
          <p class="muted">{{ 'platform.detail.noEntitlements' | transloco }}</p>
        }
      </section>

      <section class="card" aria-labelledby="inv-title">
        <h2 id="inv-title">{{ 'platform.detail.invoices' | transloco }}</h2>
        @if (o.openInvoices > 0) { <p class="alert alert-info">{{ 'platform.detail.openInvoices' | transloco: { n: o.openInvoices } }} · {{ o.openInvoicesAmount | fcMoney }}</p> }
        <table class="table">
          <caption class="sr-only">{{ 'platform.detail.invoices' | transloco }}</caption>
          <thead><tr><th scope="col">{{ 'platform.billing.number' | transloco }}</th><th scope="col">{{ 'platform.billing.period' | transloco }}</th><th scope="col">{{ 'platform.billing.dueOn' | transloco }}</th><th scope="col" class="num">{{ 'platform.billing.amount' | transloco }}</th><th scope="col">{{ 'common.status' | transloco }}</th><th scope="col"></th></tr></thead>
          <tbody>
            @for (i of o.recentInvoices; track i.id) {
              <tr>
                <th scope="row">{{ i.number }}</th>
                <td>{{ i.periodStart | fcDate }} → {{ i.periodEnd | fcDate }}</td>
                <td>{{ i.dueOn | fcDate }}</td>
                <td class="num">{{ i.amount | fcMoney }}</td>
                <td><fc-badge [tone]="invoiceToneOf(i.status)">{{ 'platform.invoiceStatus.' + i.status | transloco }}</fc-badge>@if (i.attemptCount > 0) { <small class="muted"> ×{{ i.attemptCount }}</small> }</td>
                <td class="row-actions">
                  @if (canBill() && (i.status === 'Pending' || i.status === 'Failed')) {
                    <button type="button" class="btn btn-primary" (click)="openPay(i)">{{ 'platform.billing.pay' | transloco }}</button>
                    <button type="button" class="btn" (click)="openAttempt(i)">{{ 'platform.billing.failedAttempt' | transloco }}</button>
                    <button type="button" class="btn btn-danger" (click)="voiding.set(i)">{{ 'platform.billing.void' | transloco }}</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="empty">{{ 'platform.billing.emptyInvoices' | transloco }}</td></tr>
            }
          </tbody>
        </table>
        <p class="muted">{{ 'platform.billing.notice' | transloco }}</p>
      </section>

      <section class="card" aria-labelledby="notes-title">
        <h2 id="notes-title">{{ 'platform.detail.notes' | transloco }} ({{ o.notesCount }})</h2>
        <p class="muted">{{ 'platform.detail.notesHint' | transloco }}</p>
        @if (canManage()) {
          <label class="field">
            <span>{{ 'platform.detail.newNote' | transloco }}</span>
            <textarea [value]="noteText()" maxlength="1000" (input)="noteText.set($any($event.target).value)"></textarea>
          </label>
          @if (noteError()) { <p class="alert alert-danger" role="alert">{{ noteError() }}</p> }
          <button type="button" class="btn" [disabled]="busy() || noteText().trim().length === 0" (click)="addNote()">{{ 'platform.detail.addNote' | transloco }}</button>
        }
        <ul class="timeline">
          @for (n of notes(); track n.id) {
            <li><strong>{{ n.authorName ?? '—' }}</strong> · {{ n.createdAt | fcDateTime }}<br />{{ n.text }}</li>
          }
        </ul>
      </section>

      <section class="card" aria-labelledby="history-title">
        <h2 id="history-title">{{ 'platform.detail.history' | transloco }}</h2>
        @if (o.subscription; as s) {
          <ul class="timeline">
            @for (e of s.recentEvents; track e.id) {
              <li>
                <strong>{{ eventLabel(e.type) }}</strong> · {{ e.occurredAt | fcDateTime }}
                @if (e.fromStatus && e.toStatus && e.fromStatus !== e.toStatus) { · {{ 'platform.status.' + e.fromStatus | transloco }} → {{ 'platform.status.' + e.toStatus | transloco }} }
                @if (e.detail) { <br /><small class="muted">{{ e.detail }}</small> }
              </li>
            }
          </ul>
        }
        <h3>{{ 'platform.detail.activity' | transloco }}</h3>
        <p class="muted">{{ 'platform.detail.activityHint' | transloco }}</p>
        <ul class="timeline">
          @for (a of activity(); track a.id) {
            <li><strong>{{ actionLabel(a.action) }}</strong> · {{ a.occurredAt | fcDateTime }} · {{ a.actorName ?? '—' }}</li>
          } @empty {
            <li class="muted">{{ 'platform.detail.noActivity' | transloco }}</li>
          }
        </ul>
      </section>
    }

    @if (action(); as a) {
      <fc-modal [title]="'platform.action.' + a | transloco" (closed)="action.set(null)">
        @if (actionError()) { <p class="alert alert-danger" role="alert">{{ actionError() }}</p> }
        <p class="muted">{{ 'platform.actionHint.' + a | transloco }}</p>
        @if (needsPlan(a)) {
          <label class="field">
            <span>{{ 'platform.plans.plan' | transloco }}</span>
            <select [value]="planId()" (change)="planId.set($any($event.target).value)">
              <option value="">{{ 'platform.action.choosePlan' | transloco }}</option>
              @for (p of plans(); track p.id) { <option [value]="p.id">{{ p.name }} · {{ p.current.price }} {{ p.current.currency }} · {{ 'platform.period.' + p.current.billingPeriod | transloco }}</option> }
            </select>
          </label>
        }
        @if (a === 'assign') {
          <label class="check"><input type="checkbox" [checked]="trial()" (change)="trial.set($any($event.target).checked)" /><span>{{ 'tenants.startTrial' | transloco }}</span></label>
        }
        @if (a === 'extend') {
          <label class="field"><span>{{ 'platform.action.days' | transloco }}</span><input [value]="days()" inputmode="numeric" (input)="days.set($any($event.target).value)" /></label>
        }
        @if (a === 'cancel') {
          <label class="check"><input type="checkbox" [checked]="atPeriodEnd()" (change)="atPeriodEnd.set($any($event.target).checked)" /><span>{{ 'platform.action.atPeriodEnd' | transloco }}</span></label>
        }
        @if (needsReason(a)) {
          <label class="field"><span>{{ 'platform.billing.reason' | transloco }}</span><textarea [value]="reason()" maxlength="500" (input)="reason.set($any($event.target).value)"></textarea></label>
        } @else {
          <label class="field"><span>{{ 'platform.action.note' | transloco }}</span><input [value]="reason()" maxlength="500" (input)="reason.set($any($event.target).value)" /></label>
        }
        <ng-container footer>
          <button type="button" class="btn" (click)="action.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn" [class.btn-danger]="a === 'cancel' || a === 'suspend'" [class.btn-primary]="a !== 'cancel' && a !== 'suspend'" [disabled]="busy()" (click)="runAction(a)">{{ 'platform.action.confirm' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (paying(); as i) {
      <fc-modal [title]="'platform.billing.pay' | transloco" (closed)="paying.set(null)">
        <p>{{ i.number }} · {{ i.amount | fcMoney }}</p>
        @if (payError()) { <p class="alert alert-danger" role="alert">{{ payError() }}</p> }
        <label class="field"><span>{{ 'platform.billing.receivedAmount' | transloco }}</span><input [value]="payAmount()" inputmode="decimal" (input)="payAmount.set($any($event.target).value)" /><small class="muted">{{ 'platform.billing.amountMustMatch' | transloco }}</small></label>
        <label class="field">
          <span>{{ 'platform.billing.method' | transloco }}</span>
          <select [value]="payMethod()" (change)="payMethod.set($any($event.target).value)">
            @for (m of methods; track m) { <option [value]="m">{{ 'platform.method.' + m | transloco }}</option> }
          </select>
        </label>
        <label class="field"><span>{{ 'platform.billing.reference' | transloco }}</span><input [value]="payReference()" maxlength="100" (input)="payReference.set($any($event.target).value)" /><small class="muted">{{ 'platform.billing.noCardData' | transloco }}</small></label>
        <label class="field"><span>{{ 'platform.action.note' | transloco }}</span><input [value]="payNotes()" maxlength="500" (input)="payNotes.set($any($event.target).value)" /></label>
        <ng-container footer>
          <button type="button" class="btn" (click)="paying.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="pay(i)">{{ 'platform.billing.confirmPayment' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (attempting(); as i) {
      <fc-modal [title]="'platform.billing.failedAttempt' | transloco" (closed)="attempting.set(null)">
        <p>{{ i.number }} · {{ i.amount | fcMoney }}</p>
        <p class="muted">{{ 'platform.billing.failedAttemptHint' | transloco }}</p>
        @if (payError()) { <p class="alert alert-danger" role="alert">{{ payError() }}</p> }
        <label class="field">
          <span>{{ 'platform.billing.method' | transloco }}</span>
          <select [value]="payMethod()" (change)="payMethod.set($any($event.target).value)">
            @for (m of methods; track m) { <option [value]="m">{{ 'platform.method.' + m | transloco }}</option> }
          </select>
        </label>
        <label class="field"><span>{{ 'platform.billing.reference' | transloco }}</span><input [value]="payReference()" maxlength="100" (input)="payReference.set($any($event.target).value)" /></label>
        <label class="field"><span>{{ 'platform.billing.reason' | transloco }}</span><textarea [value]="reason()" maxlength="500" (input)="reason.set($any($event.target).value)"></textarea></label>
        <ng-container footer>
          <button type="button" class="btn" (click)="attempting.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn btn-primary" [disabled]="busy() || reason().trim().length < 3" (click)="recordAttempt(i)">{{ 'platform.billing.failedAttempt' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (voiding(); as i) {
      <fc-reason-dialog
        [title]="'platform.billing.void' | transloco"
        [label]="'platform.billing.reason' | transloco"
        [confirmLabel]="'platform.billing.void' | transloco"
        [message]="i.number"
        [danger]="true"
        [minLength]="3"
        [busy]="busy()"
        [error]="payError()"
        (submitted)="voidInvoice(i, $event)"
        (cancelled)="voiding.set(null)"
      />
    }
  `,
})
export class PlatformOrganization implements OnInit {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly transloco = inject(TranslocoService);
  private readonly session = inject(SessionService);

  protected readonly methods = METHODS;
  protected readonly org = signal<OrganizationDetail | null>(null);
  protected readonly notes = signal<OrganizationNote[]>([]);
  protected readonly activity = signal<OrganizationActivity[]>([]);
  protected readonly plans = signal<CommercialPlanSummary[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly action = signal<Action | null>(null);
  protected readonly actionError = signal<string | null>(null);
  protected readonly planId = signal('');
  protected readonly trial = signal(false);
  protected readonly days = signal('7');
  protected readonly atPeriodEnd = signal(true);
  protected readonly reason = signal('');

  protected readonly paying = signal<InvoiceSummary | null>(null);
  protected readonly attempting = signal<InvoiceSummary | null>(null);
  protected readonly voiding = signal<InvoiceSummary | null>(null);
  protected readonly payAmount = signal('');
  protected readonly payMethod = signal<string>('BankTransfer');
  protected readonly payReference = signal('');
  protected readonly payNotes = signal('');
  protected readonly payError = signal<string | null>(null);
  protected readonly noteText = signal('');
  protected readonly noteError = signal<string | null>(null);
  private readonly payKey = new AttemptKey();

  protected readonly canManage = computed(() => this.session.has(Perm.PlatformSubscriptionsManage));
  protected readonly canBill = computed(() => this.session.has(Perm.PlatformBillingManage));

  private get id(): string {
    return this.route.snapshot.paramMap.get('id')!;
  }

  ngOnInit(): void {
    this.load();
  }

  protected tone(status: string) {
    return subscriptionTone(status);
  }

  protected invoiceToneOf(status: string) {
    return invoiceTone(status);
  }

  protected needsPlan(a: Action): boolean {
    return NEEDS_PLAN.includes(a);
  }

  protected needsReason(a: Action): boolean {
    return NEEDS_REASON.includes(a);
  }

  protected daysToDue(nextDue: string): number | null {
    const today = new Date(Date.now() - 5 * 3600 * 1000).toISOString().slice(0, 10);
    return daysBetween(today, nextDue);
  }

  protected used(o: OrganizationDetail, code: string): string | number {
    const u = o.usage;
    if (!u) {
      return '—';
    }

    return code === 'branches' ? u.branches : code === 'internal_users' ? u.internalUsers : code === 'members' ? u.members : this.transloco.translate('platform.detail.notMeasuredShort');
  }

  /** Acciones que tienen sentido según el estado (el servidor valida de todos modos). */
  protected available(): Action[] {
    const s = this.org()?.subscription;
    if (!s || s.status === 'Cancelled') {
      return ['assign'];
    }

    const list: Action[] = [];
    switch (s.status) {
      case 'Trialing':
        list.push('activate', 'extend', 'change-plan', 'suspend', 'cancel');
        break;
      case 'Active':
        list.push('renew', 'change-plan', 'extend', 'suspend', 'cancel');
        break;
      case 'PastDue':
        list.push('renew', 'extend', 'change-plan', 'suspend', 'cancel');
        break;
      case 'Suspended':
        list.push('reactivate', 'cancel');
        break;
      case 'Expired':
        list.push('renew', 'cancel');
        break;
      default:
        break;
    }

    if (s.cancelAtPeriodEnd) {
      list.unshift('resume-cancellation');
    }

    return list;
  }

  protected canIssue(): boolean {
    const s = this.org()?.subscription;
    return !!s && this.canBill() && !isFree(s.price) && !['Suspended', 'Cancelled'].includes(s.status) && !s.cancelAtPeriodEnd;
  }

  protected eventLabel(type: string): string {
    return this.label(`platform.eventType.${type}`, type);
  }

  protected actionLabel(action: string): string {
    return this.label(`platform.activity.${action}`, action);
  }

  private label(key: string, fallback: string): string {
    const text = this.transloco.translate(key);
    return text === key ? fallback : text;
  }

  protected openAction(action: Action): void {
    this.action.set(action);
    this.actionError.set(null);
    this.planId.set('');
    this.trial.set(false);
    this.days.set('7');
    this.atPeriodEnd.set(true);
    this.reason.set('');
    if (this.needsPlan(action) && this.plans().length === 0) {
      this.api.commercialPlans({ isActive: true, pageSize: 100 }).subscribe({ next: (r) => this.plans.set(r.items), error: (err: unknown) => this.actionError.set(this.errors.message(err)) });
    }
  }

  protected runAction(action: Action): void {
    const note = this.reason().trim() || null;
    let body: unknown;
    if (this.needsPlan(action)) {
      if (!this.planId()) {
        this.actionError.set(this.transloco.translate('platform.action.choosePlan'));
        return;
      }

      body = action === 'assign' ? { planId: this.planId(), startTrial: this.trial(), note } : { planId: this.planId(), note };
    } else if (action === 'extend') {
      const days = Number(this.days());
      if (!Number.isInteger(days) || days < 1 || days > 90 || this.reason().trim().length < 3) {
        this.actionError.set(this.transloco.translate('platform.action.extendInvalid'));
        return;
      }

      body = { days, reason: this.reason().trim() };
    } else if (action === 'suspend' || action === 'cancel') {
      if (this.reason().trim().length < 3) {
        this.actionError.set(this.transloco.translate('platform.action.reasonRequired'));
        return;
      }

      body = action === 'cancel' ? { reason: this.reason().trim(), atPeriodEnd: this.atPeriodEnd() } : { reason: this.reason().trim() };
    } else {
      body = { note };
    }

    this.busy.set(true);
    this.api.subscriptionAction(this.id, action, body).subscribe({
      next: () => {
        this.busy.set(false);
        this.action.set(null);
        this.toast.success('platform.action.done');
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.actionError.set(this.errors.message(err));
      },
    });
  }

  protected issue(): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.issueInvoice(this.id).subscribe({
      next: (invoice) => {
        this.busy.set(false);
        this.toast.success('platform.billing.issued_ok', { number: invoice.number });
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected refreshUsage(): void {
    this.busy.set(true);
    this.api.refreshOrganizationUsage(this.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success('platform.detail.usageRefreshed');
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected openPay(invoice: InvoiceSummary): void {
    this.payAmount.set(String(invoice.amount));
    this.payMethod.set('BankTransfer');
    this.payReference.set('');
    this.payNotes.set('');
    this.payError.set(null);
    this.paying.set(invoice);
  }

  protected pay(invoice: InvoiceSummary): void {
    const amount = parseAmount(this.payAmount());
    if (Number.isNaN(amount)) {
      this.payError.set(this.transloco.translate('platform.billing.amountInvalid'));
      return;
    }

    this.busy.set(true);
    this.api
      .payInvoice(
        invoice.id,
        { amount, currency: invoice.currency, method: this.payMethod() as 'BankTransfer', reference: this.payReference().trim() || null, paidAt: null, notes: this.payNotes().trim() || null },
        this.payKey.value,
      )
      .subscribe({
        next: () => {
          this.payKey.renew();
          this.busy.set(false);
          this.paying.set(null);
          this.toast.success('platform.billing.paid_ok');
          this.load();
        },
        error: (err: unknown) => {
          this.payKey.afterError(err);
          this.busy.set(false);
          this.payError.set(this.errors.message(err));
        },
      });
  }

  protected openAttempt(invoice: InvoiceSummary): void {
    this.payMethod.set('BankTransfer');
    this.payReference.set('');
    this.reason.set('');
    this.payError.set(null);
    this.attempting.set(invoice);
  }

  protected recordAttempt(invoice: InvoiceSummary): void {
    this.busy.set(true);
    this.api.recordFailedAttempt(invoice.id, { method: this.payMethod(), reference: this.payReference().trim() || null, reason: this.reason().trim() }).subscribe({
      next: () => {
        this.busy.set(false);
        this.attempting.set(null);
        this.toast.success('platform.billing.attempt_ok');
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.payError.set(this.errors.message(err));
      },
    });
  }

  protected voidInvoice(invoice: InvoiceSummary, reason: string): void {
    this.busy.set(true);
    this.payError.set(null);
    this.api.voidInvoice(invoice.id, reason).subscribe({
      next: () => {
        this.busy.set(false);
        this.voiding.set(null);
        this.toast.success('platform.billing.void_ok');
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.payError.set(this.errors.message(err));
      },
    });
  }

  protected addNote(): void {
    this.busy.set(true);
    this.noteError.set(null);
    this.api.addOrganizationNote(this.id, this.noteText().trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.noteText.set('');
        this.toast.success('platform.detail.noteAdded');
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.noteError.set(this.errors.message(err));
      },
    });
  }

  private load(): void {
    this.error.set(null);
    this.api.organization(this.id).subscribe({
      next: (o) => this.org.set(o),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
    this.api.organizationNotes(this.id, { pageSize: 20 }).subscribe({ next: (r) => this.notes.set(r.items), error: () => this.notes.set([]) });
    this.api.organizationActivity(this.id, { pageSize: 20 }).subscribe({ next: (r) => this.activity.set(r.items), error: () => this.activity.set([]) });
  }
}
