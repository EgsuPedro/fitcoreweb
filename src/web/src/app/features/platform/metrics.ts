import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { PlatformMetrics } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { FcMoney } from '../../shared/format.pipes';
import { share } from './platform-math';

/**
 * Métricas agregadas del producto SaaS. Todo son conteos y sumas de suscripciones, facturas y pagos: ningún dato de personas ni de los alumnos de
 * los gimnasios. Lo <b>contratado</b> (MRR) y lo <b>cobrado</b> son cosas distintas y cada cifra dice cómo se calcula.
 */
@Component({
  selector: 'fc-platform-metrics',
  imports: [TranslocoPipe, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'platform.metrics.title' | transloco }}</h1>
        <p class="muted">{{ 'platform.metrics.subtitle' | transloco }}</p>
      </div>
    </header>

    <section class="card filters" [attr.aria-label]="'platform.metrics.period' | transloco">
      <div class="filters-row">
        <label class="inline-field"><span>{{ 'common.from' | transloco }}</span><input type="date" [value]="from()" (change)="from.set($any($event.target).value)" /></label>
        <label class="inline-field"><span>{{ 'common.to' | transloco }}</span><input type="date" [value]="to()" (change)="to.set($any($event.target).value)" /></label>
        <button type="button" class="btn btn-primary" [disabled]="loading()" (click)="load()">{{ 'platform.metrics.apply' | transloco }}</button>
      </div>
    </section>

    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }
    @if (loading() && !data()) { <p class="muted" role="status">{{ 'common.loading' | transloco }}</p> }

    @if (data(); as m) {
      <h2>{{ 'platform.metrics.organizations' | transloco }}</h2>
      <div class="kpi-grid">
        <article class="kpi"><h3>{{ 'platform.metrics.orgTotal' | transloco }}</h3><p class="kpi-value">{{ m.organizations }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.orgSuspended' | transloco }}</h3><p class="kpi-value">{{ m.suspendedOrganizations }}</p><p class="kpi-note muted">{{ 'platform.metrics.orgSuspendedHint' | transloco }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.orgNoSubscription' | transloco }}</h3><p class="kpi-value">{{ m.organizationsWithoutSubscription }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.trials' | transloco }}</h3><p class="kpi-value">{{ m.trialsInProgress }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.pastDue' | transloco }}</h3><p class="kpi-value">{{ m.pastDue }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.overdue' | transloco }}</h3><p class="kpi-value">{{ m.overdueAmount | fcMoney }}</p><p class="kpi-note muted">{{ 'platform.metrics.overdueCount' | transloco: { n: m.overdueInvoices } }}</p></article>
      </div>

      <h2>{{ 'platform.metrics.byStatus' | transloco }}</h2>
      <ul class="bars">
        @for (s of statuses(m); track s.key) {
          <li>
            <span>{{ 'platform.status.' + s.key | transloco }}</span>
            <span class="bar-track"><span class="bar-fill" [style.width.%]="s.percent"></span></span>
            <span class="bar-value">{{ s.count }}</span>
          </li>
        }
      </ul>

      <h2>{{ 'platform.metrics.recurring' | transloco }}</h2>
      <div class="kpi-grid">
        <article class="kpi"><h3>{{ 'platform.metrics.mrr' | transloco }}</h3><p class="kpi-value">{{ m.mrr | fcMoney }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.arr' | transloco }}</h3><p class="kpi-value">{{ m.arr | fcMoney }}</p></article>
      </div>
      <p class="muted">{{ m.mrrFormula }}</p>
      <div class="card table-card">
        <table class="table">
          <caption class="sr-only">{{ 'platform.metrics.byPlan' | transloco }}</caption>
          <thead><tr><th scope="col">{{ 'platform.plans.plan' | transloco }}</th><th scope="col" class="num">{{ 'platform.metrics.subscriptions' | transloco }}</th><th scope="col" class="num">{{ 'platform.metrics.mrr' | transloco }}</th></tr></thead>
          <tbody>
            @for (p of m.byPlan; track p.planCode) {
              <tr><th scope="row">{{ p.planName }}<br /><small class="muted">{{ p.planCode }}</small></th><td class="num">{{ p.subscriptions }}</td><td class="num">{{ p.mrr | fcMoney }}</td></tr>
            } @empty {
              <tr><td colspan="3" class="empty">{{ 'platform.metrics.noPlans' | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>

      <h2>{{ 'platform.metrics.billing' | transloco: { from: m.from, to: m.to } }}</h2>
      <div class="kpi-grid">
        <article class="kpi"><h3>{{ 'platform.metrics.collected' | transloco }}</h3><p class="kpi-value">{{ m.collectedAmount | fcMoney }}</p><p class="kpi-note muted">{{ 'platform.metrics.collectedHint' | transloco }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.refunded' | transloco }}</h3><p class="kpi-value">{{ m.refundedAmount | fcMoney }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.net' | transloco }}</h3><p class="kpi-value">{{ m.netCollectedAmount | fcMoney }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.pendingInvoiced' | transloco }}</h3><p class="kpi-value">{{ m.pendingInvoicedAmount | fcMoney }}</p><p class="kpi-note muted">{{ 'platform.metrics.pendingHint' | transloco }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.renewalsPaid' | transloco }}</h3><p class="kpi-value">{{ m.renewalsPaid }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.activationsPaid' | transloco }}</h3><p class="kpi-value">{{ m.activationsPaid }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.freeRenewals' | transloco }}</h3><p class="kpi-value">{{ m.freeRenewals }}</p></article>
        <article class="kpi"><h3>{{ 'platform.metrics.cancellations' | transloco }}</h3><p class="kpi-value">{{ m.cancellations }}</p></article>
        <article class="kpi">
          <h3>{{ 'platform.metrics.conversion' | transloco }}</h3>
          @if (m.trialConversionPercent !== null) {
            <p class="kpi-value">{{ format.number(m.trialConversionPercent, 1) }} %</p>
            <p class="kpi-note muted">{{ 'platform.metrics.conversionHint' | transloco: { converted: m.trialsConverted, lost: m.trialsLost } }}</p>
          } @else {
            <p class="kpi-value">—</p>
            <p class="kpi-note muted">{{ 'platform.metrics.conversionNone' | transloco }}</p>
          }
        </article>
      </div>
    }
  `,
})
export class PlatformMetricsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  protected readonly format = inject(Formatter);

  protected readonly data = signal<PlatformMetrics | null>(null);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly from = signal('');
  protected readonly to = signal('');

  ngOnInit(): void {
    this.load();
  }

  protected statuses(m: PlatformMetrics): { key: string; count: number; percent: number }[] {
    const entries = Object.entries(m.subscriptionsByStatus);
    const total = entries.reduce((sum, [, n]) => sum + n, 0);
    return entries.map(([key, count]) => ({ key, count, percent: share(count, total) }));
  }

  protected load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.platformMetrics({ from: this.from() || null, to: this.to() || null }).subscribe({
      next: (data) => {
        this.data.set(data);
        this.from.set(data.from);
        this.to.set(data.to);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
