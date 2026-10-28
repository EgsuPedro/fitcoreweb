import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, signal, untracked } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { CashReport, CollectionsReport, OutstandingReport, PaymentMethodsReport, ReversalsReport } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDate, FcMoney } from '../../shared/format.pipes';
import { Pager } from '../../shared/pager';
import { ExportButton } from './export-button';
import { KpiCard } from './kpi-card';
import { ReportData } from './report-data';
import { ReportFilterBar, ReportFilters } from './report-filters';
import { TabBar, queryTab } from './tabs';

const PAGE_SIZE = 20;
const TABS = [
  { id: 'collections', labelKey: 'analytics.financial.tabs.collections' },
  { id: 'payment-methods', labelKey: 'analytics.financial.tabs.methods' },
  { id: 'outstanding-balances', labelKey: 'analytics.financial.tabs.balances' },
  { id: 'cash-registers', labelKey: 'analytics.financial.tabs.cash' },
  { id: 'reversals', labelKey: 'analytics.financial.tabs.reversals' },
] as const;

/** Reportes financieros. El cobrado, los saldos, la caja y las reversiones se calculan en el servidor con las reglas del sistema; aquí se leen. */
@Component({
  selector: 'fc-analytics-financial',
  imports: [TranslocoPipe, FcDate, FcMoney, Badge, Pager, KpiCard, TabBar, ReportFilterBar, ExportButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'analytics.financial.title' | transloco }}</h1>
        <p class="muted">{{ 'analytics.financial.subtitle' | transloco }}</p>
      </div>
    </header>

    <fc-report-filters />
    <fc-tab-bar [tabs]="tabs" [active]="tab.current()" (select)="tab.go($event)" />

    @switch (tab.current()) {
      @case ('collections') {
        <div class="toolbar">
          <label class="inline-field">
            <span>{{ 'analytics.financial.status' | transloco }}</span>
            <select [value]="status()" (change)="status.set($any($event.target).value); page.set(1)">
              @for (s of statuses; track s) {
                <option [value]="s">{{ 'analytics.financial.statuses.' + s | transloco }}</option>
              }
            </select>
          </label>
          <label class="inline-field">
            <span>{{ 'analytics.financial.method' | transloco }}</span>
            <select [value]="method()" (change)="method.set($any($event.target).value); page.set(1)">
              <option value="">{{ 'common.all' | transloco }}</option>
              @for (m of methods; track m) {
                <option [value]="m">{{ 'analytics.methods.' + m | transloco }}</option>
              }
            </select>
          </label>
          <fc-export-button report="Collections" [extra]="{ status: status(), method: method() || null }" />
        </div>
        @if (status() !== 'Confirmed') {
          <p class="alert alert-info">{{ 'analytics.financial.historicalNote' | transloco }}</p>
        }
        @if (collections.error()) { <p class="alert alert-danger" role="alert">{{ collections.error() }}</p> }
        @if (collections.data(); as c) {
          <div class="kpi-grid">
            @for (k of c.summary; track k.key) { <fc-kpi [kpi]="k" /> }
          </div>
          <div class="grid-2">
            <section class="section">
              <h3>{{ 'analytics.financial.byBranch' | transloco }}</h3>
              <div class="card table-card">
                <table class="table">
                  <thead><tr><th>{{ 'common.branch' | transloco }}</th><th class="num">{{ 'analytics.financial.amount' | transloco }}</th><th class="num">{{ 'analytics.financial.share' | transloco }}</th></tr></thead>
                  <tbody>
                    @for (b of c.byBranch; track b.branchId) {
                      <tr><td>{{ b.branchName }}</td><td class="num">{{ b.amount | fcMoney }}</td><td class="num">{{ b.share ?? 0 }} %</td></tr>
                    } @empty { <tr><td colspan="3" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
                  </tbody>
                </table>
              </div>
            </section>
            <section class="section">
              <h3>{{ 'analytics.financial.byPlan' | transloco }}</h3>
              <div class="card table-card">
                <table class="table">
                  <thead><tr><th>{{ 'analytics.financial.plan' | transloco }}</th><th class="num">{{ 'analytics.financial.amount' | transloco }}</th><th class="num">{{ 'analytics.financial.share' | transloco }}</th></tr></thead>
                  <tbody>
                    @for (p of c.byPlan; track $index) {
                      <tr><td>{{ p.planId ? p.planName : ('analytics.financial.noPlan' | transloco) }}</td><td class="num">{{ p.amount | fcMoney }}</td><td class="num">{{ p.share ?? 0 }} %</td></tr>
                    } @empty { <tr><td colspan="3" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
                  </tbody>
                </table>
              </div>
            </section>
          </div>
          <h3>{{ 'analytics.financial.payments' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'analytics.financial.receipt' | transloco }}</th><th>{{ 'analytics.financial.paidAt' | transloco }}</th><th>{{ 'common.branch' | transloco }}</th>
                  <th>{{ 'analytics.financial.member' | transloco }}</th><th>{{ 'analytics.financial.method' | transloco }}</th>
                  <th class="num">{{ 'analytics.financial.amount' | transloco }}</th><th>{{ 'common.status' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (p of c.payments.items; track p.paymentId) {
                  <tr>
                    <td>{{ p.receiptNumber }}</td>
                    <td>{{ when(p.paidAt, c.header.timeZone) }}</td>
                    <td>{{ p.branchName }}</td>
                    <td>{{ p.memberName }}<br /><small class="muted">{{ p.memberCode }}</small></td>
                    <td>{{ 'analytics.methods.' + p.method | transloco }}@if (p.methodDetail) { <br /><small class="muted">{{ p.methodDetail }}</small> }</td>
                    <td class="num">{{ p.amount | fcMoney }}@if (p.isPartial) { <br /><small class="muted">{{ 'analytics.financial.partial' | transloco }}</small> }</td>
                    <td><fc-badge [tone]="statusTone(p.status)">{{ 'analytics.financial.paymentStatus.' + p.status | transloco }}</fc-badge></td>
                  </tr>
                } @empty { <tr><td colspan="7" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="page()" [pageSize]="pageSize" [total]="c.payments.totalCount" (pageChange)="page.set($event)" />
        }
      }

      @case ('payment-methods') {
        <div class="toolbar"><fc-export-button report="PaymentMethods" /></div>
        @if (methodsReport.error()) { <p class="alert alert-danger" role="alert">{{ methodsReport.error() }}</p> }
        @if (methodsReport.data(); as r) {
          <p>{{ 'analytics.financial.totalCollected' | transloco }}: <strong>{{ r.total | fcMoney }}</strong> · {{ 'analytics.kpi.previous' | transloco }}: {{ r.previousTotal | fcMoney }}</p>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.financial.method' | transloco }}</th><th class="num">{{ 'analytics.financial.amount' | transloco }}</th><th class="num">{{ 'analytics.financial.payments' | transloco }}</th><th class="num">{{ 'analytics.financial.share' | transloco }}</th><th class="num">{{ 'analytics.kpi.previous' | transloco }}</th><th>{{ 'analytics.kpi.change' | transloco }}</th></tr></thead>
              <tbody>
                @for (m of r.methods; track m.method) {
                  <tr>
                    <td>{{ 'analytics.methods.' + m.method | transloco }}</td>
                    <td class="num">{{ m.amount | fcMoney }}</td>
                    <td class="num">{{ m.count }}</td>
                    <td class="num">{{ m.share === null ? '—' : m.share + ' %' }}</td>
                    <td class="num">{{ m.previousAmount | fcMoney }}</td>
                    <td>{{ variation(m.variation) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (r.otherDetails.length > 0) {
            <p class="muted">{{ 'analytics.financial.otherDetails' | transloco }}: {{ r.otherDetails.join(', ') }}</p>
          }
        }
      }

      @case ('outstanding-balances') {
        <p class="alert alert-info">{{ 'analytics.financial.snapshotNote' | transloco }}</p>
        <div class="toolbar">
          <label class="inline-field">
            <span>{{ 'analytics.financial.view' | transloco }}</span>
            <select [value]="view()" (change)="view.set($any($event.target).value); page.set(1)">
              <option value="Members">{{ 'analytics.financial.views.Members' | transloco }}</option>
              <option value="Obligations">{{ 'analytics.financial.views.Obligations' | transloco }}</option>
            </select>
          </label>
          <label class="check"><input type="checkbox" [checked]="overdueOnly()" (change)="overdueOnly.set($any($event.target).checked); page.set(1)" /> <span>{{ 'analytics.financial.overdueOnly' | transloco }}</span></label>
          <fc-export-button report="OutstandingBalances" [extra]="{ view: view(), overdueOnly: overdueOnly() }" />
        </div>
        @if (balances.error()) { <p class="alert alert-danger" role="alert">{{ balances.error() }}</p> }
        @if (balances.data(); as b) {
          <dl class="summary">
            <dt>{{ 'analytics.kpi.outstanding.name' | transloco }}</dt><dd>{{ b.outstanding | fcMoney }} ({{ b.openCount }})</dd>
            <dt>{{ 'analytics.kpi.overdue.name' | transloco }}</dt><dd>{{ b.overdue | fcMoney }} ({{ b.overdueCount }})</dd>
          </dl>
          <h3>{{ 'analytics.financial.aging' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.financial.bucket' | transloco }}</th><th class="num">{{ 'analytics.financial.count' | transloco }}</th><th class="num">{{ 'analytics.financial.amount' | transloco }}</th></tr></thead>
              <tbody>
                @for (a of b.aging; track a.label) {
                  <tr><td>{{ a.label }}</td><td class="num">{{ a.count }}</td><td class="num">{{ a.amount | fcMoney }}</td></tr>
                }
              </tbody>
            </table>
          </div>
          @if (b.members; as members) {
            <div class="card table-card">
              <table class="table">
                <thead><tr><th>{{ 'analytics.financial.member' | transloco }}</th><th class="num">{{ 'analytics.kpi.outstanding.name' | transloco }}</th><th class="num">{{ 'analytics.kpi.overdue.name' | transloco }}</th><th>{{ 'analytics.financial.oldest' | transloco }}</th><th class="num">{{ 'analytics.financial.openObligations' | transloco }}</th></tr></thead>
                <tbody>
                  @for (m of members.items; track m.memberId) {
                    <tr><td>{{ m.memberName }}<br /><small class="muted">{{ m.memberCode }}</small></td><td class="num">{{ m.outstanding | fcMoney }}</td><td class="num">{{ m.overdue | fcMoney }}</td><td>{{ m.oldestDueDate | fcDate }}</td><td class="num">{{ m.openObligations }}</td></tr>
                  } @empty { <tr><td colspan="5" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
                </tbody>
              </table>
            </div>
            <fc-pager [page]="page()" [pageSize]="pageSize" [total]="members.totalCount" (pageChange)="page.set($event)" />
          }
          @if (b.obligations; as obligations) {
            <div class="card table-card">
              <table class="table">
                <thead><tr><th>{{ 'analytics.financial.reference' | transloco }}</th><th>{{ 'analytics.financial.member' | transloco }}</th><th>{{ 'analytics.financial.plan' | transloco }}</th><th>{{ 'analytics.financial.dueDate' | transloco }}</th><th class="num">{{ 'analytics.financial.payable' | transloco }}</th><th class="num">{{ 'analytics.financial.paid' | transloco }}</th><th class="num">{{ 'analytics.financial.balance' | transloco }}</th><th>{{ 'common.status' | transloco }}</th></tr></thead>
                <tbody>
                  @for (o of obligations.items; track o.obligationId) {
                    <tr>
                      <td>{{ o.reference }}<br /><small class="muted">{{ o.concept }}</small></td>
                      <td>{{ o.memberName }}<br /><small class="muted">{{ o.memberCode }}</small></td>
                      <td>{{ o.planName ?? '—' }}</td>
                      <td>{{ o.dueDate | fcDate }}@if (o.daysOverdue > 0) { <br /><small class="muted">{{ 'analytics.financial.daysOverdue' | transloco: { n: o.daysOverdue } }}</small> }</td>
                      <td class="num">{{ o.payable | fcMoney }}</td><td class="num">{{ o.paid | fcMoney }}</td><td class="num">{{ o.balance | fcMoney }}</td>
                      <td><fc-badge [tone]="o.status === 'Overdue' ? 'danger' : 'warn'">{{ 'analytics.financial.obligationStatus.' + o.status | transloco }}</fc-badge></td>
                    </tr>
                  } @empty { <tr><td colspan="8" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
                </tbody>
              </table>
            </div>
            <fc-pager [page]="page()" [pageSize]="pageSize" [total]="obligations.totalCount" (pageChange)="page.set($event)" />
          }
        }
      }

      @case ('cash-registers') {
        <div class="toolbar">
          <label class="check"><input type="checkbox" [checked]="onlyDifference()" (change)="onlyDifference.set($any($event.target).checked); page.set(1)" /> <span>{{ 'analytics.financial.onlyDifference' | transloco }}</span></label>
          <fc-export-button report="CashRegisters" [extra]="{ onlyWithDifference: onlyDifference() }" />
        </div>
        <p class="muted">{{ 'analytics.financial.cashNote' | transloco }}</p>
        @if (cash.error()) { <p class="alert alert-danger" role="alert">{{ cash.error() }}</p> }
        @if (cash.data(); as c) {
          <dl class="summary">
            <dt>{{ 'analytics.financial.sessionsOpened' | transloco }}</dt><dd>{{ c.sessionsOpened }} ({{ c.sessionsStillOpen }} {{ 'analytics.financial.stillOpen' | transloco }}, {{ c.sessionsClosed }} {{ 'analytics.financial.closed' | transloco }})</dd>
            <dt>{{ 'analytics.financial.shortage' | transloco }}</dt><dd>{{ c.shortage | fcMoney }}</dd>
            <dt>{{ 'analytics.financial.surplus' | transloco }}</dt><dd>{{ c.surplus | fcMoney }}</dd>
          </dl>
          <h3>{{ 'analytics.financial.movements' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.financial.type' | transloco }}</th><th>{{ 'analytics.financial.direction' | transloco }}</th><th class="num">{{ 'analytics.financial.amount' | transloco }}</th><th class="num">{{ 'analytics.financial.count' | transloco }}</th></tr></thead>
              <tbody>
                @for (m of c.movements; track m.type) {
                  <tr><td>{{ 'analytics.financial.movementTypes.' + m.type | transloco }}</td><td>{{ 'analytics.financial.directions.' + m.direction | transloco }}</td><td class="num">{{ m.amount | fcMoney }}</td><td class="num">{{ m.count }}</td></tr>
                } @empty { <tr><td colspan="4" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <h3>{{ 'analytics.financial.sessions' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.financial.register' | transloco }}</th><th>{{ 'analytics.financial.openedAt' | transloco }}</th><th>{{ 'analytics.financial.closedAt' | transloco }}</th><th class="num">{{ 'analytics.financial.opening' | transloco }}</th><th class="num">{{ 'analytics.financial.expected' | transloco }}</th><th class="num">{{ 'analytics.financial.counted' | transloco }}</th><th class="num">{{ 'analytics.financial.difference' | transloco }}</th></tr></thead>
              <tbody>
                @for (s of c.sessions.items; track s.sessionId) {
                  <tr>
                    <td>{{ s.registerName }}<br /><small class="muted">{{ s.branchName }} · {{ s.openedBy }}</small></td>
                    <td>{{ when(s.openedAt, c.header.timeZone) }}</td>
                    <td>{{ s.closedAt ? when(s.closedAt, c.header.timeZone) : ('analytics.financial.open' | transloco) }}</td>
                    <td class="num">{{ s.openingAmount | fcMoney }}</td>
                    <td class="num">{{ s.expected === null ? '—' : (s.expected | fcMoney) }}</td>
                    <td class="num">{{ s.counted === null ? '—' : (s.counted | fcMoney) }}</td>
                    <td class="num">{{ s.difference === null ? '—' : (s.difference | fcMoney) }}</td>
                  </tr>
                } @empty { <tr><td colspan="7" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="page()" [pageSize]="pageSize" [total]="c.sessions.totalCount" (pageChange)="page.set($event)" />
        }
      }

      @case ('reversals') {
        <div class="toolbar"><fc-export-button report="Reversals" /></div>
        @if (reversals.error()) { <p class="alert alert-danger" role="alert">{{ reversals.error() }}</p> }
        @if (reversals.data(); as r) {
          <dl class="summary">
            <dt>{{ 'analytics.kpi.reversed.name' | transloco }}</dt><dd>{{ r.reversedAmount | fcMoney }} ({{ r.reversedCount }})</dd>
            <dt>{{ 'analytics.financial.voided' | transloco }}</dt><dd>{{ r.voidedAmount | fcMoney }} ({{ r.voidedCount }})</dd>
          </dl>
          <p class="muted">{{ 'analytics.financial.reversalsNote' | transloco }}</p>
          <h3>{{ 'analytics.financial.reversedPayments' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.financial.receipt' | transloco }}</th><th>{{ 'analytics.financial.paidAt' | transloco }}</th><th>{{ 'analytics.financial.reversedAt' | transloco }}</th><th>{{ 'analytics.financial.member' | transloco }}</th><th class="num">{{ 'analytics.financial.amount' | transloco }}</th><th>{{ 'analytics.financial.reason' | transloco }}</th></tr></thead>
              <tbody>
                @for (p of r.reversals.items; track p.paymentId) {
                  <tr><td>{{ p.receiptNumber }}</td><td>{{ when(p.paidAt, r.header.timeZone) }}</td><td>{{ when(p.reversedAt, r.header.timeZone) }}</td><td>{{ p.memberName }}<br /><small class="muted">{{ p.memberCode }}</small></td><td class="num">{{ p.amount | fcMoney }}</td><td>{{ p.reason }}<br /><small class="muted">{{ p.reversedBy }}</small></td></tr>
                } @empty { <tr><td colspan="6" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="page()" [pageSize]="pageSize" [total]="r.reversals.totalCount" (pageChange)="page.set($event)" />
          <h3>{{ 'analytics.financial.voidedObligations' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.financial.reference' | transloco }}</th><th>{{ 'analytics.financial.voidedAt' | transloco }}</th><th>{{ 'analytics.financial.member' | transloco }}</th><th class="num">{{ 'analytics.financial.payable' | transloco }}</th><th>{{ 'analytics.financial.reason' | transloco }}</th></tr></thead>
              <tbody>
                @for (o of r.voidedObligations.items; track o.obligationId) {
                  <tr><td>{{ o.reference }}<br /><small class="muted">{{ o.concept }}</small></td><td>{{ when(o.voidedAt, r.header.timeZone) }}</td><td>{{ o.memberName }}<br /><small class="muted">{{ o.memberCode }}</small></td><td class="num">{{ o.payable | fcMoney }}</td><td>{{ o.reason }}<br /><small class="muted">{{ o.voidedBy }}</small></td></tr>
                } @empty { <tr><td colspan="5" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
        }
      }
    }
  `,
})
export class AnalyticsFinancial {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly format = inject(Formatter);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly filters = inject(ReportFilters);

  protected readonly tabs = TABS;
  protected readonly tab = queryTab(TABS.map((t) => t.id), 'collections');
  protected readonly pageSize = PAGE_SIZE;
  protected readonly statuses = ['Confirmed', 'PendingVerification', 'Reversed', 'All'] as const;
  protected readonly methods = ['Cash', 'BankTransfer', 'Card', 'Other'] as const;

  protected readonly page = signal(1);
  protected readonly status = signal<string>('Confirmed');
  protected readonly method = signal('');
  protected readonly view = signal<string>('Members');
  protected readonly overdueOnly = signal(false);
  protected readonly onlyDifference = signal(false);

  protected readonly collections = new ReportData<CollectionsReport>((q) => this.api.collectionsReport(q), this.errors, this.destroyRef);
  protected readonly methodsReport = new ReportData<PaymentMethodsReport>((q) => this.api.paymentMethodsReport(q), this.errors, this.destroyRef);
  protected readonly balances = new ReportData<OutstandingReport>((q) => this.api.outstandingReport(q), this.errors, this.destroyRef);
  protected readonly cash = new ReportData<CashReport>((q) => this.api.cashReport(q), this.errors, this.destroyRef);
  protected readonly reversals = new ReportData<ReversalsReport>((q) => this.api.reversalsReport(q), this.errors, this.destroyRef);

  constructor() {
    // Los filtros compartidos, la pestaña y los filtros propios del reporte vuelven a pedir solo el reporte visible.
    effect(() => {
      const query = this.filters.query();
      const tab = this.tab.current();
      const page = this.page();
      const status = this.status();
      const method = this.method();
      const view = this.view();
      const overdueOnly = this.overdueOnly();
      const onlyDifference = this.onlyDifference();
      if (!query) {
        return;
      }
      untracked(() => {
        const paged = { page, pageSize: PAGE_SIZE };
        switch (tab) {
          case 'collections':
            this.collections.load({ ...query, ...paged, status, method });
            break;
          case 'payment-methods':
            this.methodsReport.load(query);
            break;
          case 'outstanding-balances':
            this.balances.load({ branchId: query['branchId'], view, overdueOnly, ...paged });
            break;
          case 'cash-registers':
            this.cash.load({ ...query, ...paged, onlyWithDifference: onlyDifference });
            break;
          default:
            this.reversals.load({ ...query, ...paged });
            break;
        }
      });
    });

    // Cambiar de pestaña o de filtros compartidos vuelve a la primera página.
    effect(() => {
      this.tab.current();
      this.filters.query();
      untracked(() => this.page.set(1));
    });
  }

  protected when(instant: string, zone: string): string {
    return this.format.dateTimeIn(instant, zone);
  }

  protected statusTone(status: string): 'ok' | 'warn' | 'off' {
    return status === 'Confirmed' ? 'ok' : status === 'PendingVerification' ? 'warn' : 'off';
  }

  protected variation(v: { state: string; percent: number | null; absolute: number | null }): string {
    if (v.state === 'Ok' && v.percent !== null) {
      return `${v.percent > 0 ? '+' : ''}${this.format.number(v.percent, 1)} %`;
    }
    return v.state === 'NoBaseline' ? '—' : '';
  }
}
