import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Dashboard, DashboardSeries } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { FcMoney } from '../../shared/format.pipes';
import { KpiCard } from './kpi-card';
import { PairChart } from './pair-chart';
import { ReportData } from './report-data';
import { ReportFilterBar, ReportFilters } from './report-filters';

/**
 * Dashboard ejecutivo. Todos los números los calcula el servidor (indicador, variación y gráficos); aquí solo se muestran. Cada sección llega
 * únicamente si el usuario tiene el permiso de su área: lo que no puede ver, el servidor ni lo calcula ni lo entrega.
 */
@Component({
  selector: 'fc-analytics-dashboard',
  imports: [TranslocoPipe, FcMoney, KpiCard, PairChart, ReportFilterBar],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'analytics.dashboard.title' | transloco }}</h1>
        <p class="muted">{{ 'analytics.dashboard.subtitle' | transloco }}</p>
      </div>
    </header>

    <fc-report-filters />

    @if (report.error()) {
      <p class="alert alert-danger" role="alert">{{ report.error() }}</p>
    }

    @if (report.loading() && !data()) {
      <p class="muted" role="status">{{ 'common.loading' | transloco }}</p>
    }

    @if (data(); as d) {
      @if (!d.financial && !d.members && !d.operations && !d.instructor) {
        <p class="alert alert-info">{{ 'analytics.dashboard.noSections' | transloco }}</p>
      }

      @if (d.financial; as financial) {
        <section class="section" aria-labelledby="dash-financial">
          <h2 id="dash-financial">{{ 'analytics.areas.financial' | transloco }}</h2>
          <p class="muted">{{ 'analytics.dashboard.financialNote' | transloco }}</p>
          <div class="kpi-grid">
            @for (k of financial.kpis; track k.key) {
              <fc-kpi [kpi]="k" />
            }
          </div>

          <h3>{{ 'analytics.dashboard.methods' | transloco }}</h3>
          @if (financial.methods.length === 0) {
            <p class="muted">{{ 'analytics.empty' | transloco }}</p>
          } @else {
            <ul class="bars" [attr.aria-label]="'analytics.dashboard.methods' | transloco">
              @for (m of financial.methods; track m.method) {
                <li>
                  <span class="bar-label">{{ 'analytics.methods.' + m.method | transloco }}</span>
                  <span class="bar-track" aria-hidden="true"><span class="bar-fill" [style.width.%]="m.share ?? 0"></span></span>
                  <span class="bar-value">{{ m.amount | fcMoney }} · {{ m.share ?? 0 }} % · {{ m.count }}</span>
                </li>
              }
            </ul>
          }
        </section>
      }

      @if (d.members; as members) {
        <section class="section" aria-labelledby="dash-members">
          <h2 id="dash-members">{{ 'analytics.areas.members' | transloco }}</h2>
          <label class="inline-field">
            <span>{{ 'analytics.dashboard.expiringDays' | transloco }}</span>
            <select [value]="expiringDays()" (change)="expiringDays.set(+$any($event.target).value)">
              @for (n of [7, 15, 30]; track n) {
                <option [value]="n">{{ n }}</option>
              }
            </select>
          </label>
          <div class="kpi-grid">
            @for (k of members.kpis; track k.key) {
              <fc-kpi [kpi]="k" />
            }
          </div>
        </section>
      }

      @if (d.operations; as operations) {
        <section class="section" aria-labelledby="dash-operations">
          <h2 id="dash-operations">{{ 'analytics.areas.operations' | transloco }}</h2>
          <div class="kpi-grid">
            @for (k of operations.kpis; track k.key) {
              <fc-kpi [kpi]="k" />
            }
          </div>
          <dl class="summary session-breakdown">
            <dt>{{ 'analytics.sessions.held' | transloco }}</dt><dd>{{ operations.sessions.held }}</dd>
            <dt>{{ 'analytics.sessions.cancelled' | transloco }}</dt><dd>{{ operations.sessions.cancelled }}</dd>
            <dt>{{ 'analytics.sessions.upcoming' | transloco }}</dt><dd>{{ operations.sessions.upcoming }}</dd>
            <dt>{{ 'analytics.sessions.pendingClosure' | transloco }}</dt><dd>{{ operations.sessions.pendingClosure }}</dd>
          </dl>
          <p class="muted">{{ 'analytics.sessions.pendingHint' | transloco }}</p>
        </section>
      }

      @if (d.instructor && !d.operations) {
        <section class="section" aria-labelledby="dash-instructor">
          <h2 id="dash-instructor">{{ 'analytics.areas.instructor' | transloco }}</h2>
          <p class="muted">{{ 'analytics.dashboard.instructorNote' | transloco }}</p>
          <div class="kpi-grid">
            @for (k of d.instructor.kpis; track k.key) {
              <fc-kpi [kpi]="k" />
            }
          </div>
        </section>
      }
    }

    @if (series.data(); as s) {
      @if (s.series.length > 0) {
        <section class="section" aria-labelledby="dash-evolution">
          <h2 id="dash-evolution">{{ 'analytics.dashboard.evolution' | transloco }}</h2>
          <p class="muted">{{ 'analytics.dashboard.evolutionHint' | transloco: { days: s.bucketDays } }}</p>
          <div class="chart-grid">
            @for (item of s.series; track item.key) {
              <fc-pair-chart [title]="'analytics.series.' + item.key | transloco" [current]="item.current" [previous]="item.previous" [money]="item.unit === 'Currency'" />
            }
          </div>
        </section>
      }
    }
  `,
})
export class AnalyticsDashboard {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  protected readonly filters = inject(ReportFilters);

  protected readonly expiringDays = signal(15);

  protected readonly report = new ReportData<Dashboard>((query) => this.api.dashboard(query), this.errors, inject(DestroyRef));
  protected readonly series = new ReportData<DashboardSeries>((query) => this.api.dashboardSeries(query), this.errors, inject(DestroyRef));
  protected readonly data = computed(() => this.report.data());

  constructor() {
    effect(() => {
      const query = this.filters.query();
      const expiringDays = this.expiringDays();
      if (query) {
        untracked(() => {
          this.report.load({ ...query, expiringDays });
          this.series.load(query);
        });
      }
    });
  }
}
