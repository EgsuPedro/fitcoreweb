import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, signal, untracked } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { AcquisitionsReport, ExpirationsReport, MembersSummaryReport, RenewalsReport, RetentionReport } from '../../core/api/models';
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
  { id: 'summary', labelKey: 'analytics.members.tabs.summary' },
  { id: 'acquisitions', labelKey: 'analytics.members.tabs.acquisitions' },
  { id: 'retention', labelKey: 'analytics.members.tabs.retention' },
  { id: 'renewals', labelKey: 'analytics.members.tabs.renewals' },
  { id: 'expirations', labelKey: 'analytics.members.tabs.expirations' },
] as const;

/** Reportes de alumnos y membresías: activos, altas y bajas, retención por cohorte, renovación y vencimientos. Las fórmulas están en el servidor. */
@Component({
  selector: 'fc-analytics-members',
  imports: [TranslocoPipe, FcDate, FcMoney, Badge, Pager, KpiCard, TabBar, ReportFilterBar, ExportButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'analytics.members.title' | transloco }}</h1>
        <p class="muted">{{ 'analytics.members.subtitle' | transloco }}</p>
      </div>
    </header>

    <fc-report-filters />
    <fc-tab-bar [tabs]="tabs" [active]="tab.current()" (select)="tab.go($event)" />

    @switch (tab.current()) {
      @case ('summary') {
        @if (summary.error()) { <p class="alert alert-danger" role="alert">{{ summary.error() }}</p> }
        @if (summary.data(); as s) {
          <div class="kpi-grid">
            @for (k of s.kpis; track k.key) { <fc-kpi [kpi]="k" /> }
          </div>
          <p class="muted">{{ 'analytics.members.snapshotNote' | transloco }}</p>
          <div class="grid-2">
            <section class="section">
              <h3>{{ 'analytics.members.states' | transloco }}</h3>
              <div class="card table-card">
                <table class="table">
                  <tbody>
                    @for (st of s.membershipStates; track st.key) {
                      <tr><td>{{ 'analytics.members.membershipStates.' + st.key | transloco }}</td><td class="num">{{ st.count }}</td></tr>
                    }
                  </tbody>
                </table>
              </div>
            </section>
            <section class="section">
              <h3>{{ 'analytics.members.byPlan' | transloco }}</h3>
              <div class="card table-card">
                <table class="table">
                  <tbody>
                    @for (p of s.byPlan; track p.planId) { <tr><td>{{ p.planName }}</td><td class="num">{{ p.members }}</td></tr> }
                    @empty { <tr><td class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
                  </tbody>
                </table>
              </div>
            </section>
          </div>
          <section class="section">
            <h3>{{ 'analytics.members.byBranch' | transloco }}</h3>
            <div class="card table-card">
              <table class="table">
                <thead><tr><th>{{ 'common.branch' | transloco }}</th><th class="num">{{ 'analytics.members.active' | transloco }}</th><th class="num">{{ 'analytics.members.inactive' | transloco }}</th></tr></thead>
                <tbody>
                  @for (b of s.byBranch; track b.branchId) { <tr><td>{{ b.branchName }}</td><td class="num">{{ b.active }}</td><td class="num">{{ b.inactive }}</td></tr> }
                  @empty { <tr><td colspan="3" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
                </tbody>
              </table>
            </div>
          </section>
        }
      }

      @case ('acquisitions') {
        <div class="toolbar">
          <label class="inline-field">
            <span>{{ 'analytics.members.kind' | transloco }}</span>
            <select [value]="kind()" (change)="kind.set($any($event.target).value); page.set(1)">
              @for (k of kinds; track k) { <option [value]="k">{{ 'analytics.members.kinds.' + k | transloco }}</option> }
            </select>
          </label>
          <fc-export-button report="MemberAcquisitions" [extra]="{ kind: kind() }" />
        </div>
        @if (acquisitions.error()) { <p class="alert alert-danger" role="alert">{{ acquisitions.error() }}</p> }
        @if (acquisitions.data(); as a) {
          <div class="kpi-grid">
            @for (k of a.summary; track k.key) { <fc-kpi [kpi]="k" /> }
          </div>
          <h3>{{ 'analytics.members.trend' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.members.month' | transloco }}</th><th class="num">{{ 'analytics.kpi.new_members.name' | transloco }}</th><th class="num">{{ 'analytics.kpi.new_enrollments.name' | transloco }}</th><th class="num">{{ 'analytics.kpi.renewals.name' | transloco }}</th><th class="num">{{ 'analytics.kpi.reincorporations.name' | transloco }}</th><th class="num">{{ 'analytics.kpi.gains.name' | transloco }}</th><th class="num">{{ 'analytics.kpi.losses.name' | transloco }}</th></tr></thead>
              <tbody>
                @for (t of a.trend; track t.from) {
                  <tr><td>{{ t.from | fcDate }} → {{ t.to | fcDate }}</td><td class="num">{{ t.newMembers }}</td><td class="num">{{ t.newEnrollments }}</td><td class="num">{{ t.renewals }}</td><td class="num">{{ t.reincorporations }}</td><td class="num">{{ t.gains }}</td><td class="num">{{ t.losses }}</td></tr>
                }
              </tbody>
            </table>
          </div>
          <h3>{{ 'analytics.members.history' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.members.soldAt' | transloco }}</th><th>{{ 'analytics.members.kind' | transloco }}</th><th>{{ 'analytics.financial.member' | transloco }}</th><th>{{ 'analytics.financial.plan' | transloco }}</th><th>{{ 'analytics.members.validity' | transloco }}</th><th>{{ 'common.status' | transloco }}</th><th class="num">{{ 'analytics.members.price' | transloco }}</th></tr></thead>
              <tbody>
                @for (m of a.memberships.items; track m.membershipId) {
                  <tr>
                    <td>{{ when(m.soldAt, a.header.timeZone) }}</td>
                    <td>{{ 'analytics.members.kinds.' + m.kind | transloco }}</td>
                    <td>{{ m.memberName }}<br /><small class="muted">{{ m.memberCode }} · {{ m.branchName }}</small></td>
                    <td>{{ m.planName }}</td>
                    <td>{{ m.startDate | fcDate }} → {{ m.endDate | fcDate }}</td>
                    <td><fc-badge [tone]="m.status === 'Active' ? 'ok' : m.status === 'Cancelled' ? 'off' : 'warn'">{{ 'analytics.members.membershipStates.' + m.status | transloco }}</fc-badge></td>
                    <td class="num">{{ m.agreedPrice | fcMoney }}</td>
                  </tr>
                } @empty { <tr><td colspan="7" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="page()" [pageSize]="pageSize" [total]="a.memberships.totalCount" (pageChange)="page.set($event)" />
        }
      }

      @case ('retention') {
        <div class="toolbar"><fc-export-button report="MemberRetention" /></div>
        <p class="muted">{{ 'analytics.members.retentionFormula' | transloco }}</p>
        @if (retention.error()) { <p class="alert alert-danger" role="alert">{{ retention.error() }}</p> }
        @if (retention.data(); as r) {
          <div class="kpi-grid">
            <article class="kpi">
              <header><h3>{{ 'analytics.members.retentionRate' | transloco }}</h3></header>
              <p class="kpi-value">{{ r.retentionPercent === null ? '—' : percent(r.retentionPercent) }}</p>
              @if (r.retentionPercent === null) {
                <p class="muted kpi-note">{{ 'analytics.noBaseline' | transloco }}</p>
              } @else {
                <p class="muted kpi-note">{{ 'analytics.members.cohortSummary' | transloco: { retained: r.retained, cohort: r.cohort, date: cohortDate(r.cohortDate) } }}</p>
                @if (r.previousRetentionPercent !== null) { <p class="muted kpi-note">{{ 'analytics.kpi.previous' | transloco }}: {{ percent(r.previousRetentionPercent) }}</p> }
              }
            </article>
          </div>
          <div class="grid-2">
            <section class="section">
              <h3>{{ 'analytics.members.byBranch' | transloco }}</h3>
              <div class="card table-card"><table class="table"><tbody>
                @for (g of r.byBranch; track g.key) { <tr><td>{{ g.name }}</td><td class="num">{{ g.retained }} / {{ g.cohort }}</td><td class="num">{{ g.retentionPercent === null ? '—' : percent(g.retentionPercent) }}</td></tr> }
                @empty { <tr><td class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody></table></div>
            </section>
            <section class="section">
              <h3>{{ 'analytics.members.byPlan' | transloco }}</h3>
              <div class="card table-card"><table class="table"><tbody>
                @for (g of r.byPlan; track g.key) { <tr><td>{{ g.name }}</td><td class="num">{{ g.retained }} / {{ g.cohort }}</td><td class="num">{{ g.retentionPercent === null ? '—' : percent(g.retentionPercent) }}</td></tr> }
                @empty { <tr><td class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody></table></div>
            </section>
          </div>
          <h3>{{ 'analytics.members.lost' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.financial.member' | transloco }}</th><th>{{ 'common.branch' | transloco }}</th><th>{{ 'analytics.members.lastPlan' | transloco }}</th><th>{{ 'analytics.members.lastEnd' | transloco }}</th></tr></thead>
              <tbody>
                @for (m of r.lostMembers.items; track m.memberCode) {
                  <tr><td>{{ m.memberName }}<br /><small class="muted">{{ m.memberCode }}</small></td><td>{{ m.branchName }}</td><td>{{ m.lastPlan ?? '—' }}</td><td>{{ m.lastEndDate | fcDate }}</td></tr>
                } @empty { <tr><td colspan="4" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="page()" [pageSize]="pageSize" [total]="r.lostMembers.totalCount" (pageChange)="page.set($event)" />
        }
      }

      @case ('renewals') {
        <div class="toolbar"><fc-export-button report="MemberRenewals" /></div>
        <p class="muted">{{ 'analytics.members.renewalFormula' | transloco }}</p>
        @if (renewals.error()) { <p class="alert alert-danger" role="alert">{{ renewals.error() }}</p> }
        @if (renewals.data(); as r) {
          <div class="kpi-grid">
            <article class="kpi">
              <header><h3>{{ 'analytics.members.renewalRate' | transloco }}</h3></header>
              <p class="kpi-value">{{ r.ratePercent === null ? '—' : percent(r.ratePercent) }}</p>
              @if (r.ratePercent === null) {
                <p class="muted kpi-note">{{ 'analytics.noBaseline' | transloco }}</p>
              } @else {
                <p class="muted kpi-note">{{ 'analytics.members.renewalSummary' | transloco: { renewed: r.renewed, due: r.due } }}</p>
              }
            </article>
          </div>
          <div class="grid-2">
            <section class="section">
              <h3>{{ 'analytics.members.byPlan' | transloco }}</h3>
              <div class="card table-card"><table class="table"><tbody>
                @for (g of r.byPlan; track g.key) { <tr><td>{{ g.name }}</td><td class="num">{{ g.renewed }} / {{ g.due }}</td><td class="num">{{ g.ratePercent === null ? '—' : percent(g.ratePercent) }}</td></tr> }
                @empty { <tr><td class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody></table></div>
            </section>
            <section class="section">
              <h3>{{ 'analytics.members.byBranch' | transloco }}</h3>
              <div class="card table-card"><table class="table"><tbody>
                @for (g of r.byBranch; track g.key) { <tr><td>{{ g.name }}</td><td class="num">{{ g.renewed }} / {{ g.due }}</td><td class="num">{{ g.ratePercent === null ? '—' : percent(g.ratePercent) }}</td></tr> }
                @empty { <tr><td class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody></table></div>
            </section>
          </div>
          <h3>{{ 'analytics.members.notRenewed' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.financial.member' | transloco }}</th><th>{{ 'common.branch' | transloco }}</th><th>{{ 'analytics.financial.plan' | transloco }}</th><th>{{ 'analytics.members.ended' | transloco }}</th></tr></thead>
              <tbody>
                @for (m of r.notRenewedMembers.items; track m.memberCode + m.endDate) {
                  <tr><td>{{ m.memberName }}<br /><small class="muted">{{ m.memberCode }}</small></td><td>{{ m.branchName }}</td><td>{{ m.planName }}</td><td>{{ m.endDate | fcDate }}<br /><small class="muted">{{ 'analytics.members.daysSince' | transloco: { n: m.daysSinceEnd } }}</small></td></tr>
                } @empty { <tr><td colspan="4" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="page()" [pageSize]="pageSize" [total]="r.notRenewedMembers.totalCount" (pageChange)="page.set($event)" />
        }
      }

      @case ('expirations') {
        <p class="alert alert-info">{{ 'analytics.financial.snapshotNote' | transloco }}</p>
        <div class="toolbar">
          <label class="inline-field">
            <span>{{ 'analytics.members.within' | transloco }}</span>
            <select [value]="days()" (change)="days.set(+$any($event.target).value); page.set(1)">
              @for (n of dayOptions; track n) { <option [value]="n">{{ 'analytics.members.daysOption' | transloco: { n } }}</option> }
            </select>
          </label>
          <label class="check"><input type="checkbox" [checked]="hideRenewed()" (change)="hideRenewed.set($any($event.target).checked); page.set(1)" /> <span>{{ 'analytics.members.hideRenewed' | transloco }}</span></label>
          <fc-export-button report="MemberExpirations" [extra]="{ days: days(), hideRenewed: hideRenewed() }" />
        </div>
        @if (expirations.error()) { <p class="alert alert-danger" role="alert">{{ expirations.error() }}</p> }
        @if (expirations.data(); as e) {
          <dl class="summary">
            <dt>{{ 'analytics.members.expiringTotal' | transloco }}</dt><dd>{{ e.total }}</dd>
            <dt>{{ 'analytics.members.alreadyRenewed' | transloco }}</dt><dd>{{ e.alreadyRenewed }}</dd>
            @for (b of e.buckets; track b.key) { <dt>{{ 'analytics.members.bucket' | transloco: { range: b.key } }}</dt><dd>{{ b.count }}</dd> }
          </dl>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'analytics.financial.member' | transloco }}</th><th>{{ 'analytics.financial.plan' | transloco }}</th><th>{{ 'analytics.members.endsOn' | transloco }}</th><th>{{ 'analytics.members.renewed' | transloco }}</th><th class="num">{{ 'analytics.financial.balance' | transloco }}</th></tr></thead>
              <tbody>
                @for (m of e.memberships.items; track m.membershipId) {
                  <tr>
                    <td>{{ m.memberName }}<br /><small class="muted">{{ m.memberCode }} · {{ m.branchName }}</small></td>
                    <td>{{ m.planName }}</td>
                    <td>{{ m.endDate | fcDate }}<br /><small class="muted">{{ 'analytics.members.daysLeft' | transloco: { n: m.daysLeft } }}</small></td>
                    <td><fc-badge [tone]="m.alreadyRenewed ? 'ok' : 'warn'">{{ (m.alreadyRenewed ? 'common.yes' : 'common.no') | transloco }}</fc-badge></td>
                    <td class="num">{{ m.balance | fcMoney }}</td>
                  </tr>
                } @empty { <tr><td colspan="5" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="page()" [pageSize]="pageSize" [total]="e.memberships.totalCount" (pageChange)="page.set($event)" />
        }
      }
    }
  `,
})
export class AnalyticsMembers {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly format = inject(Formatter);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly filters = inject(ReportFilters);

  protected readonly tabs = TABS;
  protected readonly tab = queryTab(TABS.map((t) => t.id), 'summary');
  protected readonly pageSize = PAGE_SIZE;
  protected readonly kinds = ['Any', 'New', 'Renewal', 'Reincorporation'] as const;
  protected readonly dayOptions = [7, 15, 30, 60, 90] as const;

  protected readonly page = signal(1);
  protected readonly kind = signal<string>('Any');
  protected readonly days = signal(30);
  protected readonly hideRenewed = signal(false);

  protected readonly summary = new ReportData<MembersSummaryReport>((q) => this.api.membersSummaryReport(q), this.errors, this.destroyRef);
  protected readonly acquisitions = new ReportData<AcquisitionsReport>((q) => this.api.acquisitionsReport(q), this.errors, this.destroyRef);
  protected readonly retention = new ReportData<RetentionReport>((q) => this.api.retentionReport(q), this.errors, this.destroyRef);
  protected readonly renewals = new ReportData<RenewalsReport>((q) => this.api.renewalsReport(q), this.errors, this.destroyRef);
  protected readonly expirations = new ReportData<ExpirationsReport>((q) => this.api.expirationsReport(q), this.errors, this.destroyRef);

  constructor() {
    effect(() => {
      const query = this.filters.query();
      const tab = this.tab.current();
      const page = this.page();
      const kind = this.kind();
      const days = this.days();
      const hideRenewed = this.hideRenewed();
      if (!query) {
        return;
      }
      untracked(() => {
        const paged = { page, pageSize: PAGE_SIZE };
        switch (tab) {
          case 'summary':
            this.summary.load(query);
            break;
          case 'acquisitions':
            this.acquisitions.load({ ...query, ...paged, kind });
            break;
          case 'retention':
            this.retention.load({ ...query, ...paged });
            break;
          case 'renewals':
            this.renewals.load({ ...query, ...paged });
            break;
          default:
            this.expirations.load({ branchId: query['branchId'], days, hideRenewed, ...paged });
            break;
        }
      });
    });

    effect(() => {
      this.tab.current();
      this.filters.query();
      untracked(() => this.page.set(1));
    });
  }

  protected when(instant: string, zone: string): string {
    return this.format.dateTimeIn(instant, zone);
  }

  protected percent(value: number): string {
    return `${this.format.number(value, 1)} %`;
  }

  protected cohortDate(date: string): string {
    return this.format.dateOnly(date);
  }
}
