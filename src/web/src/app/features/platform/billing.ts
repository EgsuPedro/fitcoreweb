import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { BillingSummary, InvoiceSummary, PaymentProviderStatus, ProviderEvent, SubscriptionPayment } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDate, FcDateTime, FcMoney } from '../../shared/format.pipes';
import { AttemptKey } from '../../shared/idempotency';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';
import { TabBar, queryTab } from '../analytics/tabs';
import { invoiceTone, parseAmount } from './platform-math';

const TABS = [
  { id: 'invoices', labelKey: 'platform.billing.tabs.invoices' },
  { id: 'payments', labelKey: 'platform.billing.tabs.payments' },
  { id: 'provider', labelKey: 'platform.billing.tabs.provider' },
] as const;
const OUTCOMES = ['Applied', 'Rejected', 'Ignored'] as const;
const STATUSES = ['Pending', 'Failed', 'Paid', 'Void', 'Refunded', 'PartiallyRefunded'] as const;

/**
 * Cobros comerciales de FIT360: facturas internas (no tributarias), pagos y resumen. Cobrar, anular y reembolsar se hacen desde la ficha de cada
 * organización (el reembolso, aquí, sobre el pago). Lo emitido, lo cobrado, lo reembolsado y lo pendiente son sumas distintas y se rotulan aparte.
 */
@Component({
  selector: 'fc-platform-billing',
  imports: [RouterLink, TranslocoPipe, Badge, FcDate, FcDateTime, FcMoney, Modal, Pager, TabBar],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'platform.billing.title' | transloco }}</h1>
        <p class="muted">{{ 'platform.billing.subtitle' | transloco }}</p>
      </div>
      <button type="button" class="btn" [disabled]="running()" (click)="runLifecycle()">{{ 'platform.billing.runLifecycle' | transloco }}</button>
    </header>
    <p class="alert alert-info">{{ 'platform.billing.notice' | transloco }}</p>

    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    <section class="card filters" [attr.aria-label]="'platform.billing.summary' | transloco">
      <div class="filters-row">
        <label class="inline-field"><span>{{ 'common.from' | transloco }}</span><input type="date" [value]="from()" (change)="from.set($any($event.target).value)" /></label>
        <label class="inline-field"><span>{{ 'common.to' | transloco }}</span><input type="date" [value]="to()" (change)="to.set($any($event.target).value)" /></label>
        <button type="button" class="btn btn-primary" (click)="loadSummary()">{{ 'platform.metrics.apply' | transloco }}</button>
      </div>
    </section>

    @if (summary(); as s) {
      <div class="kpi-grid">
        <article class="kpi"><h3>{{ 'platform.billing.issued' | transloco }}</h3><p class="kpi-value">{{ s.issuedAmount | fcMoney }}</p><p class="kpi-note muted">{{ 'platform.billing.count' | transloco: { n: s.issuedCount } }}</p></article>
        <article class="kpi"><h3>{{ 'platform.billing.collected' | transloco }}</h3><p class="kpi-value">{{ s.collectedAmount | fcMoney }}</p><p class="kpi-note muted">{{ 'platform.billing.count' | transloco: { n: s.collectedCount } }}</p></article>
        <article class="kpi"><h3>{{ 'platform.billing.refunded' | transloco }}</h3><p class="kpi-value">{{ s.refundedAmount | fcMoney }}</p></article>
        <article class="kpi"><h3>{{ 'platform.billing.net' | transloco }}</h3><p class="kpi-value">{{ s.netCollectedAmount | fcMoney }}</p></article>
        <article class="kpi"><h3>{{ 'platform.billing.pending' | transloco }}</h3><p class="kpi-value">{{ s.pendingAmount | fcMoney }}</p><p class="kpi-note muted">{{ 'platform.billing.count' | transloco: { n: s.pendingCount } }}</p></article>
        <article class="kpi"><h3>{{ 'platform.billing.overdue' | transloco }}</h3><p class="kpi-value">{{ s.overdueAmount | fcMoney }}</p><p class="kpi-note muted">{{ 'platform.billing.count' | transloco: { n: s.overdueCount } }}</p></article>
      </div>
    }

    <fc-tab-bar [tabs]="tabs" [active]="tab.current()" label="platform.billing.tabs.label" (select)="tab.go($event)" />

    @if (tab.current() === 'invoices') {
      <section class="card filters">
        <div class="filters-row">
          <label class="inline-field"><span>{{ 'common.search' | transloco }}</span><input type="search" (input)="invoices.search($any($event.target).value)" /></label>
          <label class="inline-field">
            <span>{{ 'common.status' | transloco }}</span>
            <select (change)="invoices.set({ status: $any($event.target).value })">
              <option value="">{{ 'common.all' | transloco }}</option>
              @for (st of statuses; track st) { <option [value]="st">{{ 'platform.invoiceStatus.' + st | transloco }}</option> }
            </select>
          </label>
          <label class="check"><input type="checkbox" (change)="invoices.set({ overdue: $any($event.target).checked })" /><span>{{ 'platform.billing.overdueOnly' | transloco }}</span></label>
        </div>
      </section>
      @if (invoices.error()) { <p class="alert alert-danger" role="alert">{{ invoices.error() }}</p> }
      <div class="card table-card">
        <table class="table">
          <caption class="sr-only">{{ 'platform.billing.tabs.invoices' | transloco }}</caption>
          <thead>
            <tr>
              <th scope="col">{{ 'platform.billing.number' | transloco }}</th>
              <th scope="col">{{ 'platform.organizations.organization' | transloco }}</th>
              <th scope="col">{{ 'platform.billing.period' | transloco }}</th>
              <th scope="col">{{ 'platform.billing.dueOn' | transloco }}</th>
              <th scope="col" class="num">{{ 'platform.billing.amount' | transloco }}</th>
              <th scope="col">{{ 'common.status' | transloco }}</th>
            </tr>
          </thead>
          <tbody>
            @for (i of invoices.items(); track i.id) {
              <tr>
                <th scope="row">{{ i.number }}<br /><small class="muted">{{ 'platform.invoiceKind.' + i.kind | transloco }}</small></th>
                <td><a [routerLink]="['/platform/organizations', i.tenantId]">{{ i.tenantName }}</a></td>
                <td>{{ i.periodStart | fcDate }} → {{ i.periodEnd | fcDate }}</td>
                <td>{{ i.dueOn | fcDate }}</td>
                <td class="num">{{ i.amount | fcMoney }}@if (i.refundedAmount > 0) { <br /><small class="muted">−{{ i.refundedAmount | fcMoney }}</small> }</td>
                <td><fc-badge [tone]="tone(i.status)">{{ 'platform.invoiceStatus.' + i.status | transloco }}</fc-badge></td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="empty">{{ (invoices.loading() ? 'common.loading' : 'platform.billing.emptyInvoices') | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>
      <fc-pager [page]="invoices.page()" [pageSize]="invoices.pageSize" [total]="invoices.total()" (pageChange)="invoices.goTo($event)" />
    } @else if (tab.current() === 'payments') {
      @if (payments.error()) { <p class="alert alert-danger" role="alert">{{ payments.error() }}</p> }
      <div class="card table-card">
        <table class="table">
          <caption class="sr-only">{{ 'platform.billing.tabs.payments' | transloco }}</caption>
          <thead>
            <tr>
              <th scope="col">{{ 'platform.billing.paidAt' | transloco }}</th>
              <th scope="col">{{ 'platform.organizations.organization' | transloco }}</th>
              <th scope="col">{{ 'platform.billing.number' | transloco }}</th>
              <th scope="col">{{ 'platform.billing.method' | transloco }}</th>
              <th scope="col" class="num">{{ 'platform.billing.amount' | transloco }}</th>
              <th scope="col"></th>
            </tr>
          </thead>
          <tbody>
            @for (p of payments.items(); track p.id) {
              <tr>
                <td>{{ p.paidAt | fcDateTime }}</td>
                <td><a [routerLink]="['/platform/organizations', p.tenantId]">{{ p.tenantName }}</a></td>
                <td>{{ p.invoiceNumber }}<br /><small class="muted">{{ p.reference ?? '' }}</small></td>
                <td>{{ 'platform.method.' + p.method | transloco }}</td>
                <td class="num">{{ p.amount | fcMoney }}@if (p.refundedAmount > 0) { <br /><small class="muted">−{{ p.refundedAmount | fcMoney }}</small> }</td>
                <td class="row-actions">
                  @if (p.amount - p.refundedAmount > 0) {
                    <button type="button" class="btn" (click)="openRefund(p)">{{ 'platform.billing.refund' | transloco }}</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="empty">{{ (payments.loading() ? 'common.loading' : 'platform.billing.emptyPayments') | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>
      <fc-pager [page]="payments.page()" [pageSize]="payments.pageSize" [total]="payments.total()" (pageChange)="payments.goTo($event)" />
    } @else {
      <section class="card">
        <h2>{{ 'platform.provider.title' | transloco }}</h2>
        @if (providerStatus(); as s) {
          @if (s.configured) {
            <p><fc-badge tone="ok">{{ 'platform.provider.configured' | transloco }}</fc-badge> <strong>{{ s.provider }}</strong></p>
            <dl class="summary">
              <div><dt>{{ 'platform.provider.endpoint' | transloco }}</dt><dd><code>POST {{ s.webhookPath }}</code></dd></div>
              <div><dt>{{ 'platform.provider.header' | transloco }}</dt><dd><code>{{ s.signatureHeader }}</code></dd></div>
              <div><dt>{{ 'platform.provider.tolerance' | transloco }}</dt><dd>{{ s.toleranceSeconds }} s</dd></div>
            </dl>
            @if (s.rotationInProgress) { <p class="alert alert-info">{{ 'platform.provider.rotation' | transloco }}</p> }
          } @else {
            <p><fc-badge tone="off">{{ 'platform.provider.notConfigured' | transloco }}</fc-badge></p>
            <p class="muted">{{ 'platform.provider.notConfiguredHint' | transloco }}</p>
          }
        }
        <p class="muted">{{ 'platform.provider.hint' | transloco }}</p>
      </section>

      <section class="card filters">
        <div class="filters-row">
          <label class="inline-field">
            <span>{{ 'platform.provider.result' | transloco }}</span>
            <select (change)="events.set({ outcome: $any($event.target).value })">
              <option value="">{{ 'common.all' | transloco }}</option>
              @for (o of outcomes; track o) { <option [value]="o">{{ 'platform.provider.outcome.' + o | transloco }}</option> }
            </select>
          </label>
        </div>
      </section>
      @if (events.error()) { <p class="alert alert-danger" role="alert">{{ events.error() }}</p> }
      <div class="card table-card">
        <table class="table">
          <caption class="sr-only">{{ 'platform.billing.tabs.provider' | transloco }}</caption>
          <thead>
            <tr>
              <th scope="col">{{ 'platform.provider.received' | transloco }}</th>
              <th scope="col">{{ 'platform.provider.event' | transloco }}</th>
              <th scope="col">{{ 'platform.billing.number' | transloco }}</th>
              <th scope="col" class="num">{{ 'platform.billing.amount' | transloco }}</th>
              <th scope="col">{{ 'platform.provider.result' | transloco }}</th>
            </tr>
          </thead>
          <tbody>
            @for (e of events.items(); track e.id) {
              <tr>
                <td>{{ e.receivedAt | fcDateTime }}</td>
                <td>{{ 'platform.provider.type.' + e.type | transloco }}<br /><small class="muted">{{ e.externalEventId }}</small></td>
                <td>
                  @if (e.tenantId) { <a [routerLink]="['/platform/organizations', e.tenantId]">{{ e.tenantName }}</a><br /> }
                  {{ e.invoiceNumber }}
                </td>
                <td class="num">@if (e.amount !== null) { {{ e.amount | fcMoney }} <small class="muted">{{ e.currency }}</small> }</td>
                <td>
                  <fc-badge [tone]="outcomeTone(e.outcome)">{{ 'platform.provider.outcome.' + e.outcome | transloco }}</fc-badge>
                  @if (e.outcomeCode) { <br /><small class="muted">{{ outcomeCodeText(e.outcomeCode) }}</small> }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="empty">{{ (events.loading() ? 'common.loading' : 'platform.provider.empty') | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>
      <fc-pager [page]="events.page()" [pageSize]="events.pageSize" [total]="events.total()" (pageChange)="events.goTo($event)" />
    }

    @if (refunding(); as p) {
      <fc-modal [title]="'platform.billing.refund' | transloco" (closed)="refunding.set(null)">
        <p>{{ 'platform.billing.refundAvailable' | transloco: { invoice: p.invoiceNumber, amount: p.amount - p.refundedAmount } }}</p>
        @if (refundError()) { <p class="alert alert-danger" role="alert">{{ refundError() }}</p> }
        <label class="field"><span>{{ 'platform.billing.refundAmount' | transloco }}</span><input [value]="refundAmount()" inputmode="decimal" (input)="refundAmount.set($any($event.target).value)" /></label>
        <label class="field"><span>{{ 'platform.billing.reason' | transloco }}</span><textarea [value]="refundReason()" maxlength="500" (input)="refundReason.set($any($event.target).value)"></textarea></label>
        <p class="muted">{{ 'platform.billing.refundHint' | transloco }}</p>
        <ng-container footer>
          <button type="button" class="btn" (click)="refunding.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn btn-danger" [disabled]="busy()" (click)="refund(p)">{{ 'platform.billing.refund' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }
  `,
})
export class PlatformBilling implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly transloco = inject(TranslocoService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly tabs = TABS;
  protected readonly statuses = STATUSES;
  protected readonly tab = queryTab(TABS.map((t) => t.id), 'invoices');
  protected readonly invoices = new ListState<InvoiceSummary>((q) => this.api.invoices(q), this.errors, this.destroyRef);
  protected readonly events = new ListState<ProviderEvent>((q) => this.api.providerEvents(q), this.errors, this.destroyRef);
  protected readonly outcomes = OUTCOMES;
  protected readonly providerStatus = signal<PaymentProviderStatus | null>(null);
  protected readonly payments = new ListState<SubscriptionPayment>((q) => this.api.subscriptionPayments(q), this.errors, this.destroyRef);

  protected readonly summary = signal<BillingSummary | null>(null);
  protected readonly from = signal('');
  protected readonly to = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly running = signal(false);
  protected readonly refunding = signal<SubscriptionPayment | null>(null);
  protected readonly refundAmount = signal('');
  protected readonly refundReason = signal('');
  protected readonly refundError = signal<string | null>(null);
  protected readonly busy = signal(false);
  private readonly key = new AttemptKey();

  ngOnInit(): void {
    this.invoices.load();
    this.payments.load();
    this.events.load();
    this.api.paymentProviderStatus().subscribe({ next: (s) => this.providerStatus.set(s), error: () => this.providerStatus.set(null) });
    this.loadSummary();
  }

  protected outcomeTone(outcome: string) {
    return outcome === 'Applied' ? 'ok' : outcome === 'Rejected' ? 'danger' : 'off';
  }

  /** Motivo por el que un evento no se aplicó, en palabras; si es un código desconocido se muestra tal cual. */
  protected outcomeCodeText(code: string): string {
    const key = 'platform.provider.code.' + code;
    const text = this.transloco.translate(key);
    return text === key ? code : text;
  }

  protected tone(status: string) {
    return invoiceTone(status);
  }

  protected loadSummary(): void {
    this.error.set(null);
    this.api.billingSummary({ from: this.from() || null, to: this.to() || null }).subscribe({
      next: (s) => {
        this.summary.set(s);
        this.from.set(s.from);
        this.to.set(s.to);
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  protected runLifecycle(): void {
    this.running.set(true);
    this.api.runSubscriptionLifecycle().subscribe({
      next: (r) => {
        this.running.set(false);
        this.toast.success('platform.billing.lifecycleDone', { examined: r.examined, invoices: r.invoicesIssued, pastDue: r.markedPastDue });
        this.invoices.load();
        this.loadSummary();
      },
      error: (err: unknown) => {
        this.running.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected openRefund(payment: SubscriptionPayment): void {
    this.refundAmount.set(String(payment.amount - payment.refundedAmount));
    this.refundReason.set('');
    this.refundError.set(null);
    this.refunding.set(payment);
  }

  protected refund(payment: SubscriptionPayment): void {
    const amount = parseAmount(this.refundAmount());
    if (Number.isNaN(amount) || amount <= 0 || amount > payment.amount - payment.refundedAmount || this.refundReason().trim().length < 3) {
      this.refundError.set(this.transloco.translate('platform.billing.refundInvalid'));
      return;
    }

    this.busy.set(true);
    this.api.refundSubscriptionPayment(payment.id, { amount, reason: this.refundReason().trim() }, this.key.value).subscribe({
      next: () => {
        this.key.renew();
        this.busy.set(false);
        this.refunding.set(null);
        this.toast.success('platform.billing.refunded_ok');
        this.payments.load();
        this.invoices.load();
        this.loadSummary();
      },
      error: (err: unknown) => {
        this.key.afterError(err);
        this.busy.set(false);
        this.refundError.set(this.errors.message(err));
      },
    });
  }
}
