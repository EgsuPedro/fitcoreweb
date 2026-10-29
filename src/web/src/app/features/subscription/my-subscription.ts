import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { AvailablePlan, InvoiceSummary, SubscriptionNotice, SubscriptionRequestItem, SubscriptionRequestKind, SubscriptionUsage } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDate, FcDateTime, FcMoney } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';
import { TabBar, queryTab } from '../analytics/tabs';
import { invoiceTone, subscriptionTone } from '../platform/platform-math';
import { requestTone, usagePercent } from './subscription-math';
import { SubscriptionStatus } from './subscription-status';

const TABS = [
  { id: 'summary', labelKey: 'mySubscription.tabs.summary' },
  { id: 'usage', labelKey: 'mySubscription.tabs.usage' },
  { id: 'plans', labelKey: 'mySubscription.tabs.plans' },
  { id: 'invoices', labelKey: 'mySubscription.tabs.invoices' },
  { id: 'notices', labelKey: 'mySubscription.tabs.notices' },
  { id: 'requests', labelKey: 'mySubscription.tabs.requests' },
] as const;

interface Asking {
  kind: SubscriptionRequestKind;
  plan: AvailablePlan | null;
}

/**
 * «Mi suscripción»: lo que el gimnasio contrató con FIT360 (plan, período, consumo, facturas, avisos). El gimnasio <b>no cambia</b> su contrato aquí:
 * pide un cambio de plan, la cancelación o la reactivación, y el equipo de plataforma lo aprueba o lo rechaza con una razón. No hay cobro en línea ni
 * se guardan datos de tarjeta; los documentos son internos, no comprobantes tributarios.
 */
@Component({
  selector: 'fc-my-subscription',
  imports: [TranslocoPipe, Badge, FcDate, FcDateTime, FcMoney, Modal, Pager, TabBar],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'mySubscription.title' | transloco }}</h1>
        <p class="muted">{{ 'mySubscription.subtitle' | transloco }}</p>
      </div>
    </header>

    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    @if (status.current(); as s) {
      <fc-tab-bar [tabs]="tabs" [active]="tab.current()" label="mySubscription.tabs.label" (select)="tab.go($event)" />

      @switch (tab.current()) {
        @case ('summary') {
          <section class="card">
            <h2>
              {{ s.planName }}
              <fc-badge [tone]="statusTone(s.status)">{{ 'platform.status.' + s.status | transloco }}</fc-badge>
            </h2>
            <dl class="summary">
              <div><dt>{{ 'mySubscription.price' | transloco }}</dt><dd>{{ s.isFree ? ('mySubscription.free' | transloco) : (s.price | fcMoney) }} · {{ 'platform.period.' + s.billingPeriod | transloco }}</dd></div>
              <div><dt>{{ 'mySubscription.accessMode' | transloco }}</dt><dd>{{ 'platform.accessMode.' + s.accessMode | transloco }}</dd></div>
              <div><dt>{{ 'mySubscription.currentPeriod' | transloco }}</dt><dd>{{ s.currentPeriodStart | fcDate }} → {{ s.currentPeriodEnd | fcDate }}</dd></div>
              <div><dt>{{ 'mySubscription.nextDue' | transloco }}</dt><dd>{{ s.nextDueOn | fcDate }}</dd></div>
              @if (s.trialEndsOn) { <div><dt>{{ 'mySubscription.trialEnds' | transloco }}</dt><dd>{{ s.trialEndsOn | fcDate }}</dd></div> }
              @if (s.graceEndsOn) { <div><dt>{{ 'mySubscription.graceEnds' | transloco }}</dt><dd>{{ s.graceEndsOn | fcDate }}</dd></div> }
              @if (s.cancelAtPeriodEnd) { <div><dt>{{ 'mySubscription.cancelScheduled' | transloco }}</dt><dd>{{ s.currentPeriodEnd | fcDate }}</dd></div> }
              @if (s.pendingPlanName) { <div><dt>{{ 'mySubscription.pendingPlan' | transloco }}</dt><dd>{{ s.pendingPlanName }} · {{ 'mySubscription.pendingPlanHint' | transloco }}</dd></div> }
            </dl>
            @if (s.accessMode === 'Restricted') {
              <p class="alert alert-warning">{{ 'mySubscription.restrictedExplain' | transloco }}</p>
            }
            @if (s.accessMode === 'Grace') {
              <p class="alert alert-info">{{ 'mySubscription.graceExplain' | transloco }}</p>
            }
          </section>

          @if (s.openInvoice; as inv) {
            <section class="card">
              <h2>{{ 'mySubscription.openInvoice' | transloco }}</h2>
              <p>
                <strong>{{ inv.number }}</strong> · {{ inv.amount | fcMoney }} · {{ 'mySubscription.dueOn' | transloco }} {{ inv.dueOn | fcDate }}
                @if (inv.overdue) { <fc-badge tone="danger">{{ 'mySubscription.overdue' | transloco }}</fc-badge> }
              </p>
              <p class="muted">{{ 'mySubscription.howToPay' | transloco }}</p>
            </section>
          }

          <section class="card">
            <h2>{{ 'mySubscription.requestsPending' | transloco }}</h2>
            @for (r of s.pendingRequests; track r.id) {
              <div class="subcard">
                <p>
                  <strong>{{ 'mySubscription.kind.' + r.kind | transloco }}</strong>
                  @if (r.requestedPlanName) { → {{ r.requestedPlanName }} }
                  · {{ r.createdAt | fcDateTime }}
                </p>
                @if (r.message) { <p class="muted">{{ r.message }}</p> }
                @if (canManage()) {
                  <button type="button" class="btn" [disabled]="busy()" (click)="withdraw(r)">{{ 'mySubscription.withdraw' | transloco }}</button>
                }
              </div>
            } @empty {
              <p class="muted">{{ 'mySubscription.noPending' | transloco }}</p>
            }
            @if (canManage()) {
              <div class="row-actions">
                <button type="button" class="btn" (click)="tab.go('plans')">{{ 'mySubscription.askChange' | transloco }}</button>
                @if (canAsk(s, 'Cancellation')) {
                  <button type="button" class="btn btn-danger" (click)="ask('Cancellation', null)">{{ 'mySubscription.askCancel' | transloco }}</button>
                }
                @if (canAsk(s, 'Reactivation')) {
                  <button type="button" class="btn btn-primary" (click)="ask('Reactivation', null)">{{ 'mySubscription.askReactivate' | transloco }}</button>
                }
              </div>
              <p class="muted">{{ 'mySubscription.requestHint' | transloco }}</p>
            }
          </section>
        }

        @case ('usage') {
          @if (usage(); as u) {
            <section class="card">
              <h2>{{ 'mySubscription.limits' | transloco }}</h2>
              @for (l of u.limits; track l.code) {
                <div class="meter-row">
                  <span>{{ 'platform.limit.' + l.code | transloco }}</span>
                  <span class="meter" [class.meter-ok]="!l.reached" role="img" [attr.aria-label]="meterLabel(l)"><span [style.--w]="percent(l) ?? 0"></span></span>
                  <span>
                    @if (!l.measured) { {{ 'mySubscription.notMeasured' | transloco }} }
                    @else { {{ l.used }} / {{ l.limit === null ? ('mySubscription.unlimited' | transloco) : l.limit }} }
                  </span>
                </div>
                @if (l.reached) { <p class="alert alert-warning">{{ 'mySubscription.limitReached' | transloco }}</p> }
              }
            </section>
            <section class="card">
              <h2>{{ 'mySubscription.features' | transloco }}</h2>
              <ul class="timeline">
                @for (f of u.features; track f.code) {
                  <li>
                    <fc-badge [tone]="f.enabled ? 'ok' : 'off'">{{ (f.enabled ? 'mySubscription.included' : 'mySubscription.notIncluded') | transloco }}</fc-badge>
                    {{ 'platform.feature.' + f.code | transloco }}
                  </li>
                }
              </ul>
            </section>
          } @else {
            <p class="muted" role="status">{{ 'common.loading' | transloco }}</p>
          }
        }

        @case ('plans') {
          <p class="muted">{{ 'mySubscription.plansIntro' | transloco }}</p>
          @for (p of plans(); track p.plan.id) {
            <section class="card">
              <h2>
                {{ p.plan.name }}
                @if (p.isCurrent) { <fc-badge tone="ok">{{ 'mySubscription.currentPlan' | transloco }}</fc-badge> }
              </h2>
              @if (p.plan.description) { <p class="muted">{{ p.plan.description }}</p> }
              <p>
                <strong>{{ p.plan.current.price === 0 ? ('mySubscription.free' | transloco) : (p.plan.current.price | fcMoney) }}</strong>
                · {{ 'platform.period.' + p.plan.current.billingPeriod | transloco }}
              </p>
              <ul class="timeline">
                @for (l of p.plan.current.limits; track l.code) {
                  <li>{{ 'platform.limit.' + l.code | transloco }}: {{ l.value === null ? ('mySubscription.unlimited' | transloco) : l.value }}</li>
                }
              </ul>
              <p class="muted">{{ featureNames(p) }}</p>
              @if (!p.isCurrent) {
                <p class="muted">{{ 'mySubscription.changeKind.' + p.changeKind | transloco }}</p>
                @if (canManage()) {
                  <button type="button" class="btn btn-primary" [disabled]="hasPending('ChangePlan')" (click)="ask('ChangePlan', p)">{{ 'mySubscription.askThisPlan' | transloco }}</button>
                }
              }
            </section>
          } @empty {
            <p class="muted">{{ 'mySubscription.noPlans' | transloco }}</p>
          }
          @if (hasPending('ChangePlan')) { <p class="alert alert-info">{{ 'mySubscription.changePending' | transloco }}</p> }
        }

        @case ('invoices') {
          <p class="muted">{{ 'mySubscription.invoiceNotice' | transloco }}</p>
          <div class="card table-card">
            <table class="table">
              <caption class="sr-only">{{ 'mySubscription.tabs.invoices' | transloco }}</caption>
              <thead>
                <tr>
                  <th scope="col">{{ 'platform.billing.number' | transloco }}</th>
                  <th scope="col">{{ 'platform.billing.period' | transloco }}</th>
                  <th scope="col">{{ 'platform.billing.dueOn' | transloco }}</th>
                  <th scope="col" class="num">{{ 'platform.billing.amount' | transloco }}</th>
                  <th scope="col">{{ 'common.status' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (i of invoices.items(); track i.id) {
                  <tr>
                    <th scope="row">{{ i.number }}</th>
                    <td>{{ i.periodStart | fcDate }} → {{ i.periodEnd | fcDate }}</td>
                    <td>{{ i.dueOn | fcDate }}</td>
                    <td class="num">{{ i.amount | fcMoney }}</td>
                    <td><fc-badge [tone]="invoiceStatusTone(i.status)">{{ 'platform.invoiceStatus.' + i.status | transloco }}</fc-badge></td>
                  </tr>
                } @empty {
                  <tr><td colspan="5" class="empty">{{ (invoices.loading() ? 'common.loading' : 'platform.billing.emptyInvoices') | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="invoices.page()" [pageSize]="invoices.pageSize" [total]="invoices.total()" (pageChange)="invoices.goTo($event)" />
        }

        @case ('notices') {
          <div class="row-actions">
            <button type="button" class="btn" [disabled]="busy() || s.unreadNotices === 0" (click)="markAllRead()">{{ 'mySubscription.markAllRead' | transloco }}</button>
          </div>
          <ul class="timeline">
            @for (n of notices.items(); track n.id) {
              <li class="subcard" [class.unread]="!n.readAt">
                <p>
                  @if (!n.readAt) { <fc-badge tone="info">{{ 'mySubscription.new' | transloco }}</fc-badge> }
                  <strong>{{ 'mySubscription.notice.' + n.type + '.title' | transloco }}</strong>
                  <span class="muted"> · {{ n.createdAt | fcDateTime }}</span>
                </p>
                <p>{{ 'mySubscription.notice.' + n.type + '.body' | transloco: { plan: n.planName ?? '', date: n.relatedDate ? (n.relatedDate | fcDate) : '', ref: n.reference ?? '' } }}</p>
                @if (!n.readAt) {
                  <button type="button" class="btn btn-ghost" [disabled]="busy()" (click)="markRead(n)">{{ 'mySubscription.markRead' | transloco }}</button>
                }
              </li>
            } @empty {
              <li class="muted">{{ (notices.loading() ? 'common.loading' : 'mySubscription.noNotices') | transloco }}</li>
            }
          </ul>
          <fc-pager [page]="notices.page()" [pageSize]="notices.pageSize" [total]="notices.total()" (pageChange)="notices.goTo($event)" />
        }

        @case ('requests') {
          <div class="card table-card">
            <table class="table">
              <caption class="sr-only">{{ 'mySubscription.tabs.requests' | transloco }}</caption>
              <thead>
                <tr>
                  <th scope="col">{{ 'mySubscription.request' | transloco }}</th>
                  <th scope="col">{{ 'common.status' | transloco }}</th>
                  <th scope="col">{{ 'mySubscription.answer' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (r of requests.items(); track r.id) {
                  <tr>
                    <th scope="row">
                      {{ 'mySubscription.kind.' + r.kind | transloco }}@if (r.requestedPlanName) { → {{ r.requestedPlanName }} }<br />
                      <small class="muted">{{ r.createdAt | fcDateTime }}</small>
                      @if (r.message) { <br /><small>{{ r.message }}</small> }
                    </th>
                    <td><fc-badge [tone]="reqTone(r.status)">{{ 'mySubscription.requestStatus.' + r.status | transloco }}</fc-badge></td>
                    <td>@if (r.resolvedAt) { {{ r.resolvedAt | fcDateTime }}<br /> } {{ r.resolutionNote ?? '' }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="3" class="empty">{{ (requests.loading() ? 'common.loading' : 'mySubscription.noRequests') | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="requests.page()" [pageSize]="requests.pageSize" [total]="requests.total()" (pageChange)="requests.goTo($event)" />
        }
      }
    } @else if (!error()) {
      <p class="muted" role="status">{{ 'common.loading' | transloco }}</p>
    }

    @if (asking(); as a) {
      <fc-modal [title]="('mySubscription.ask.' + a.kind) | transloco" (closed)="asking.set(null)">
        @if (askError()) { <p class="alert alert-danger" role="alert">{{ askError() }}</p> }
        @if (a.plan; as p) { <p><strong>{{ p.plan.name }}</strong> · {{ 'mySubscription.changeKind.' + p.changeKind | transloco }}</p> }
        <label class="field">
          <span>{{ (a.kind === 'Cancellation' ? 'mySubscription.reasonRequired' : 'mySubscription.messageOptional') | transloco }}</span>
          <textarea [value]="message()" maxlength="500" (input)="message.set($any($event.target).value)"></textarea>
          <small class="muted">{{ 'mySubscription.noCardData' | transloco }}</small>
        </label>
        <p class="muted">{{ 'mySubscription.askHint' | transloco }}</p>
        <ng-container footer>
          <button type="button" class="btn" (click)="asking.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn" [class.btn-danger]="a.kind === 'Cancellation'" [class.btn-primary]="a.kind !== 'Cancellation'" [disabled]="busy()" (click)="submit(a)">{{ 'mySubscription.send' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }
  `,
})
export class MySubscriptionPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly transloco = inject(TranslocoService);
  private readonly session = inject(SessionService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly status = inject(SubscriptionStatus);

  protected readonly tabs = TABS;
  protected readonly tab = queryTab(TABS.map((t) => t.id), 'summary');
  protected readonly invoices = new ListState<InvoiceSummary>((q) => this.api.mySubscriptionInvoices(q), this.errors, this.destroyRef);
  protected readonly notices = new ListState<SubscriptionNotice>((q) => this.api.mySubscriptionNotices(q), this.errors, this.destroyRef);
  protected readonly requests = new ListState<SubscriptionRequestItem>((q) => this.api.mySubscriptionRequests(q), this.errors, this.destroyRef);

  protected readonly usage = signal<SubscriptionUsage | null>(null);
  protected readonly plans = signal<AvailablePlan[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly asking = signal<Asking | null>(null);
  protected readonly message = signal('');
  protected readonly askError = signal<string | null>(null);
  protected readonly canManage = computed(() => this.session.has(Perm.SubscriptionManage));

  ngOnInit(): void {
    this.status.refresh();
    this.invoices.load();
    this.notices.load();
    this.requests.load();
    this.api.mySubscriptionUsage().subscribe({ next: (u) => this.usage.set(u), error: (err: unknown) => this.error.set(this.errors.message(err)) });
    this.api.mySubscriptionPlans().subscribe({ next: (p) => this.plans.set(p), error: () => this.plans.set([]) });
  }

  protected statusTone(status: string) {
    return subscriptionTone(status);
  }

  protected invoiceStatusTone(status: string) {
    return invoiceTone(status);
  }

  protected reqTone(status: string) {
    return requestTone(status);
  }

  protected percent(l: { used: number | null; limit: number | null }): number | null {
    return usagePercent(l.used, l.limit);
  }

  protected meterLabel(l: { code: string; used: number | null; limit: number | null }): string {
    return `${l.code}: ${l.used ?? '—'} / ${l.limit ?? '∞'}`;
  }

  protected featureNames(p: AvailablePlan): string {
    return p.plan.current.features.join(' · ');
  }

  protected hasPending(kind: SubscriptionRequestKind): boolean {
    return (this.status.current()?.pendingRequests ?? []).some((r) => r.kind === kind);
  }

  /** Qué se puede pedir según el estado (el servidor lo vuelve a validar). */
  protected canAsk(s: { status: string; cancelAtPeriodEnd: boolean }, kind: SubscriptionRequestKind): boolean {
    if (this.hasPending(kind)) {
      return false;
    }

    return kind === 'Cancellation'
      ? ['Trialing', 'Active', 'PastDue'].includes(s.status) && !s.cancelAtPeriodEnd
      : ['Suspended', 'Expired', 'Cancelled'].includes(s.status) || s.cancelAtPeriodEnd;
  }

  protected ask(kind: SubscriptionRequestKind, plan: AvailablePlan | null): void {
    this.message.set('');
    this.askError.set(null);
    this.asking.set({ kind, plan });
  }

  protected submit(a: Asking): void {
    const message = this.message().trim();
    if (a.kind === 'Cancellation' && message.length === 0) {
      this.askError.set(this.transloco.translate('errors.subscription_requests.message_required'));
      return;
    }

    this.busy.set(true);
    this.api.createSubscriptionRequest({ kind: a.kind, planId: a.plan?.plan.id ?? null, message: message || null }).subscribe({
      next: () => {
        this.busy.set(false);
        this.asking.set(null);
        this.toast.success('mySubscription.sent');
        this.afterChange();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.askError.set(this.errors.message(err));
      },
    });
  }

  protected withdraw(request: SubscriptionRequestItem): void {
    this.busy.set(true);
    this.api.withdrawSubscriptionRequest(request.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success('mySubscription.withdrawn');
        this.afterChange();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected markRead(notice: SubscriptionNotice): void {
    this.mark(notice.id);
  }

  protected markAllRead(): void {
    this.mark(undefined);
  }

  private mark(id: string | undefined): void {
    this.busy.set(true);
    this.api.markSubscriptionNoticesRead(id).subscribe({
      next: () => {
        this.busy.set(false);
        this.notices.load();
        this.status.refresh();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  private afterChange(): void {
    this.status.refresh();
    this.requests.load();
  }
}
