import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { ReceivablesSummary } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { BranchCatalog } from '../../shared/branch-catalog';
import { FcDate, FcMoney } from '../../shared/format.pipes';

/** Cartera: lo que los alumnos deben hoy, por antigüedad, sucursal y principales deudores. */
@Component({
  selector: 'fc-receivables',
  imports: [RouterLink, TranslocoPipe, FcDate, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'billing.receivables.title' | transloco }}</h1>
    </header>
    <p class="muted">{{ 'billing.receivables.intro' | transloco }}</p>

    <div class="toolbar">
      <select [attr.aria-label]="'common.branch' | transloco" (change)="load($any($event.target).value)">
        <option value="">{{ 'common.allBranches' | transloco }}</option>
        @for (branch of catalog.branches(); track branch.id) {
          <option [value]="branch.id">{{ branch.name }}</option>
        }
      </select>
    </div>

    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    @if (summary(); as s) {
      <div class="stats">
        <div class="stat card"><span class="muted">{{ 'billing.receivables.outstanding' | transloco }}</span><strong>{{ s.outstanding | fcMoney }}</strong></div>
        <div class="stat card"><span class="muted">{{ 'billing.receivables.overdue' | transloco }}</span><strong>{{ s.overdue | fcMoney }}</strong></div>
        <div class="stat card"><span class="muted">{{ 'billing.receivables.openCount' | transloco }}</span><strong>{{ s.openCount }}</strong></div>
        <div class="stat card"><span class="muted">{{ 'billing.receivables.overdueCount' | transloco }}</span><strong>{{ s.overdueCount }}</strong></div>
      </div>

      @if (s.openCount === 0) {
        <p class="empty">{{ 'billing.receivables.empty' | transloco }}</p>
      } @else {
        <section class="section">
          <h2>{{ 'billing.receivables.aging' | transloco }}</h2>
          <div class="card section">
            @for (bucket of s.aging; track bucket.label) {
              <div class="bar-row">
                <span class="bar-label">{{ 'billing.receivables.label.' + bucket.label | transloco }}</span>
                <span class="bar" [style.--w]="width(bucket.amount, s)"></span>
                <span class="num">{{ bucket.amount | fcMoney }} <small class="muted">({{ bucket.count }})</small></span>
              </div>
            }
          </div>
        </section>

        <section class="section">
          <h2>{{ 'billing.receivables.byBranch' | transloco }}</h2>
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'common.branch' | transloco }}</th>
                  <th class="num">{{ 'billing.receivables.outstanding' | transloco }}</th>
                  <th class="num">{{ 'billing.receivables.overdue' | transloco }}</th>
                  <th class="num">{{ 'billing.receivables.obligations' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (b of s.byBranch; track b.branchId) {
                  <tr>
                    <td>{{ b.branchName }}</td>
                    <td class="num">{{ b.outstanding | fcMoney }}</td>
                    <td class="num">{{ b.overdue | fcMoney }}</td>
                    <td class="num">{{ b.openCount }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>

        <section class="section">
          <h2>{{ 'billing.receivables.debtors' | transloco }}</h2>
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'attendance.member' | transloco }}</th>
                  <th>{{ 'billing.receivables.phone' | transloco }}</th>
                  <th class="num">{{ 'billing.receivables.outstanding' | transloco }}</th>
                  <th class="num">{{ 'billing.receivables.overdue' | transloco }}</th>
                  <th>{{ 'billing.receivables.oldestDue' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (d of s.topDebtors; track d.memberId) {
                  <tr>
                    <td><a [routerLink]="['/members', d.memberId]">{{ d.memberName }}</a> <small class="muted">{{ d.memberCode }}</small></td>
                    <td>{{ d.phone ?? '—' }}</td>
                    <td class="num">{{ d.outstanding | fcMoney }}</td>
                    <td class="num">{{ d.overdue | fcMoney }}</td>
                    <td>{{ d.oldestDueDate | fcDate }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }
    } @else if (!error()) {
      <p class="muted">{{ 'common.loading' | transloco }}</p>
    }
  `,
})
export class Receivables implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly summary = signal<ReceivablesSummary | null>(null);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.catalog.ensure();
    this.load('');
  }

  protected load(branchId: string): void {
    this.error.set(null);
    this.api.receivables({ branchId: branchId || undefined, top: 20 }).subscribe({
      next: (summary) => this.summary.set(summary),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  protected width(amount: number, summary: ReceivablesSummary): number {
    const max = Math.max(...summary.aging.map((bucket) => bucket.amount), 0);
    return max > 0 ? Math.round((amount / max) * 100) : 0;
  }
}
