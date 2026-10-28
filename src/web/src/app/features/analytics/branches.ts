import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, untracked } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { BranchComparison, BranchComparisonRow } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcMoney } from '../../shared/format.pipes';
import { ExportButton } from './export-button';
import { PairChart } from './pair-chart';
import { ReportData } from './report-data';
import { ReportFilterBar, ReportFilters } from './report-filters';

/**
 * Comparación de sucursales: el mismo período y las mismas definiciones para todas. Una sucursal sin actividad aparece en cero y marcada «Sin datos»;
 * un área que el usuario no puede ver llega vacía (no se muestra una columna con datos que no le corresponden).
 */
@Component({
  selector: 'fc-analytics-branches',
  imports: [NgTemplateOutlet, TranslocoPipe, FcMoney, Badge, ExportButton, ReportFilterBar, PairChart],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'analytics.branches.title' | transloco }}</h1>
        <p class="muted">{{ 'analytics.branches.subtitle' | transloco }}</p>
      </div>
      <fc-export-button report="BranchComparison" />
    </header>

    <fc-report-filters />

    @if (report.error()) { <p class="alert alert-danger" role="alert">{{ report.error() }}</p> }
    @if (report.loading() && !report.data()) { <p class="muted" role="status">{{ 'common.loading' | transloco }}</p> }

    @if (report.data(); as r) {
      @if (r.omittedSections.length > 0) {
        <p class="alert alert-info">{{ 'analytics.branches.omitted' | transloco }}</p>
      }
      <div class="card table-card">
        <table class="table comparison">
          <caption class="sr-only">{{ 'analytics.branches.title' | transloco }}</caption>
          <thead>
            <tr>
              <th scope="col">{{ 'common.branch' | transloco }}</th>
              @if (hasFinancial(r)) {
                <th scope="col" class="num">{{ 'analytics.kpi.collected.name' | transloco }}</th>
                <th scope="col" class="num">{{ 'analytics.financial.share' | transloco }}</th>
                <th scope="col" class="num">{{ 'analytics.kpi.outstanding.name' | transloco }}</th>
                <th scope="col" class="num">{{ 'analytics.kpi.overdue.name' | transloco }}</th>
              }
              @if (hasMembers(r)) {
                <th scope="col" class="num">{{ 'analytics.kpi.active_members.name' | transloco }}</th>
                <th scope="col" class="num">{{ 'analytics.kpi.new_enrollments.name' | transloco }}</th>
                <th scope="col" class="num">{{ 'analytics.kpi.renewals.name' | transloco }}</th>
              }
              @if (hasOperations(r)) {
                <th scope="col" class="num">{{ 'analytics.kpi.visits.name' | transloco }}</th>
                <th scope="col" class="num">{{ 'analytics.kpi.occupancy_reserved.name' | transloco }}</th>
                <th scope="col" class="num">{{ 'analytics.kpi.sessions_cancelled.name' | transloco }}</th>
                <th scope="col" class="num">{{ 'analytics.branches.cancelRate' | transloco }}</th>
              }
            </tr>
          </thead>
          <tbody>
            @for (item of rows(r); track item.key) {
              <ng-container [ngTemplateOutlet]="row" [ngTemplateOutletContext]="{ $implicit: item.row, total: item.total }" />
            }
          </tbody>
        </table>
      </div>
      <p class="muted">{{ 'analytics.branches.note' | transloco }}</p>

      @if (r.months.length > 0) {
        <section class="section">
          <h2>{{ 'analytics.branches.evolution' | transloco }}</h2>
          <div class="chart-grid">
            @for (e of r.evolution; track e.branchId) {
              @if (e.collected) {
                <fc-pair-chart [title]="e.branchName + ' · ' + ('analytics.kpi.collected.name' | transloco)" [current]="serie(r, e.collected)" [previous]="empty" [money]="true" />
              }
            }
          </div>
          <div class="card table-card">
            <table class="table">
              <caption class="sr-only">{{ 'analytics.branches.evolution' | transloco }}</caption>
              <thead>
                <tr>
                  <th scope="col">{{ 'common.branch' | transloco }}</th>
                  @for (m of r.months; track m.from) { <th scope="col" class="num">{{ monthLabel(m.from) }}</th> }
                </tr>
              </thead>
              <tbody>
                @for (e of r.evolution; track e.branchId) {
                  @if (e.collected) {
                    <tr><th scope="row">{{ e.branchName }} · {{ 'analytics.kpi.collected.name' | transloco }}</th>@for (v of e.collected; track $index) { <td class="num">{{ v | fcMoney }}</td> }</tr>
                  }
                  @if (e.newEnrollments) {
                    <tr><th scope="row">{{ e.branchName }} · {{ 'analytics.kpi.new_enrollments.name' | transloco }}</th>@for (v of e.newEnrollments; track $index) { <td class="num">{{ v }}</td> }</tr>
                  }
                  @if (e.visits) {
                    <tr><th scope="row">{{ e.branchName }} · {{ 'analytics.kpi.visits.name' | transloco }}</th>@for (v of e.visits; track $index) { <td class="num">{{ v }}</td> }</tr>
                  }
                }
              </tbody>
            </table>
          </div>
        </section>
      }
    }

    <ng-template #row let-b let-total="total">
      <tr [class.total-row]="total">
        <th scope="row">
          {{ total ? ('analytics.branches.total' | transloco) : b.branchName }}
          @if (!total && !b.hasActivity) { <fc-badge tone="off">{{ 'analytics.branches.noActivity' | transloco }}</fc-badge> }
        </th>
        @if (b.financial) {
          <td class="num">{{ b.financial.collected | fcMoney }}@if (b.financial.collectedVariation.state === 'Ok' && b.financial.collectedVariation.percent !== null) { <br /><small class="muted">{{ signed(b.financial.collectedVariation.percent) }} %</small> }</td>
          <td class="num">{{ b.financial.collectedShare === null ? '—' : b.financial.collectedShare + ' %' }}</td>
          <td class="num">{{ b.financial.outstanding | fcMoney }}</td>
          <td class="num">{{ b.financial.overdue | fcMoney }}</td>
        }
        @if (b.members) {
          <td class="num">{{ b.members.activeMembers }}</td>
          <td class="num">{{ b.members.newEnrollments }}</td>
          <td class="num">{{ b.members.renewals }}</td>
        }
        @if (b.operations) {
          <td class="num">{{ b.operations.visits }}</td>
          <td class="num">{{ b.operations.occupancyPercent === null ? ('analytics.noData' | transloco) : b.operations.occupancyPercent + ' %' }}</td>
          <td class="num">{{ b.operations.sessionsCancelled }}</td>
          <td class="num">{{ b.operations.cancellationRatePercent === null ? '—' : b.operations.cancellationRatePercent + ' %' }}</td>
        }
      </tr>
    </ng-template>
  `,
})
export class AnalyticsBranches {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly format = inject(Formatter);
  protected readonly filters = inject(ReportFilters);

  protected readonly report = new ReportData<BranchComparison>((q) => this.api.branchComparison(q), this.errors, inject(DestroyRef));
  protected readonly empty = { starts: [] as string[], values: [] as number[] };

  constructor() {
    effect(() => {
      const query = this.filters.query();
      if (query) {
        untracked(() => this.report.load(query));
      }
    });
  }

  protected rows(r: BranchComparison): { key: string; row: BranchComparisonRow; total: boolean }[] {
    return [...r.branches.map((row) => ({ key: row.branchId, row, total: false })), { key: 'total', row: r.total, total: true }];
  }

  protected hasFinancial(r: BranchComparison): boolean {
    return r.total.financial !== null;
  }

  protected hasMembers(r: BranchComparison): boolean {
    return r.total.members !== null;
  }

  protected hasOperations(r: BranchComparison): boolean {
    return r.total.operations !== null;
  }

  protected serie(r: BranchComparison, values: readonly number[]) {
    return { starts: r.months.map((m) => m.from), values };
  }

  protected monthLabel(from: string): string {
    return this.format.monthTitle(from);
  }

  protected signed(value: number): string {
    return `${value > 0 ? '+' : ''}${this.format.number(value, 1)}`;
  }

}
