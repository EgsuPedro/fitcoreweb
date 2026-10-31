import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { SriInvoiceListItem, SriInvoicingSummary } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, invoiceTone } from '../../shared/badge';
import { FcDate, FcMoney } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';

/** Estados que, juntos, significan «una persona tiene que mirar esto». */
export const ATTENTION_STATUSES = ['Rejected', 'NotAuthorized'] as const;

/** Estados en trámite: el sistema los avanza solo. */
export const IN_PROGRESS_STATUSES = ['PendingSignature', 'SignedDocument', 'PendingSubmission', 'Received', 'Processing'] as const;

/** Suma de los comprobantes que están en alguno de los estados. */
export function countOf(summary: SriInvoicingSummary | null, statuses: readonly string[]): number {
  return summary ? statuses.reduce((total, s) => total + (summary.countsByStatus[s] ?? 0), 0) : 0;
}

/**
 * Panel y listado de comprobantes electrónicos: cifras para saber de un vistazo si algo requiere atención y la lista de comprobantes con su estado ante el SRI. Para facturar una venta se
 * entra desde sus cobros; aquí se consulta, se filtra y se abre cada comprobante.
 */
@Component({
  selector: 'fc-invoices',
  imports: [RouterLink, TranslocoPipe, Badge, Pager, FcDate, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'invoicing.invoices.title' | transloco }}</h1>
        <p class="muted">{{ 'invoicing.invoices.subtitle' | transloco }}</p>
      </div>
      <a class="btn" routerLink="/invoicing/settings">{{ 'nav.invoicingSettings' | transloco }}</a>
    </header>

    @if (summaryError()) { <p class="alert alert-danger" role="alert">{{ summaryError() }}</p> }

    @if (summary(); as s) {
      <section class="stats" [attr.aria-label]="'invoicing.invoices.panel' | transloco">
        <div class="stat stat-ok">
          <div class="stat-label">{{ 'invoicing.invoices.stats.monthAuthorized' | transloco }}</div>
          <div class="stat-value">{{ s.monthAuthorizedCount }}</div>
          <div class="muted">{{ s.monthAuthorizedTotal | fcMoney }}</div>
        </div>
        <div class="stat">
          <div class="stat-label">{{ 'invoicing.invoices.stats.inProgress' | transloco }}</div>
          <div class="stat-value">{{ inProgress() }}</div>
          <div class="muted">{{ 'invoicing.invoices.stats.inProgressHint' | transloco }}</div>
        </div>
        <div class="stat" [class.stat-danger]="s.needsAttention > 0">
          <div class="stat-label">{{ 'invoicing.invoices.stats.attention' | transloco }}</div>
          <div class="stat-value">{{ s.needsAttention }}</div>
          <div class="muted">{{ 'invoicing.invoices.stats.attentionHint' | transloco }}</div>
          @if (s.needsAttention > 0) {
            <button type="button" class="btn btn-ghost" (click)="filter(s.countsByStatus['Rejected'] > 0 ? 'Rejected' : 'NotAuthorized')">{{ 'invoicing.invoices.showThem' | transloco }}</button>
          }
        </div>
        <div class="stat" [class.stat-danger]="s.emailsFailed > 0">
          <div class="stat-label">{{ 'invoicing.invoices.stats.emailsFailed' | transloco }}</div>
          <div class="stat-value">{{ s.emailsFailed }}</div>
          <div class="muted">{{ 'invoicing.invoices.stats.emailsFailedHint' | transloco }}</div>
        </div>
      </section>
      @if (s.parked > 0) { <p class="alert alert-warning" role="status">{{ 'invoicing.invoices.parked' | transloco: { count: s.parked } }}</p> }
    }

    <div class="toolbar">
      <input
        type="search"
        class="search"
        [placeholder]="'invoicing.invoices.searchHint' | transloco"
        [attr.aria-label]="'invoicing.invoices.searchHint' | transloco"
        (input)="list.search($any($event.target).value)"
      />
      <select [attr.aria-label]="'common.status' | transloco" [value]="status()" (change)="filter($any($event.target).value)">
        <option value="">{{ 'common.all' | transloco }}</option>
        @for (st of statuses; track st) { <option [value]="st">{{ 'enums.invoiceStatus.' + st | transloco }}</option> }
      </select>
    </div>

    @if (list.error()) { <p class="alert alert-danger" role="alert">{{ list.error() }}</p> }

    <div class="card table-card">
      <table class="table">
        <caption class="sr-only">{{ 'invoicing.invoices.title' | transloco }}</caption>
        <thead>
          <tr>
            <th scope="col">{{ 'invoicing.invoices.number' | transloco }}</th>
            <th scope="col">{{ 'invoicing.invoices.issueDate' | transloco }}</th>
            <th scope="col">{{ 'invoicing.invoices.buyer' | transloco }}</th>
            <th scope="col" class="num">{{ 'invoicing.invoices.total' | transloco }}</th>
            <th scope="col">{{ 'common.status' | transloco }}</th>
            <th scope="col"><span class="sr-only">{{ 'common.actions' | transloco }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (i of list.items(); track i.id) {
            <tr>
              <td>
                <a [routerLink]="['/invoicing/invoices', i.id]"><strong>{{ i.number }}</strong></a>
                @if (i.environment === 'Test') { <fc-badge tone="neutral">{{ 'invoicing.invoices.testBadge' | transloco }}</fc-badge> }
              </td>
              <td>{{ i.issueDate | fcDate }}</td>
              <td>{{ i.buyerName }}<br /><small class="muted">{{ i.buyerIdNumber }}</small></td>
              <td class="num">{{ i.total | fcMoney }}</td>
              <td><fc-badge [tone]="tone(i.status)">{{ 'enums.invoiceStatus.' + i.status | transloco }}</fc-badge></td>
              <td class="actions"><a class="btn" [routerLink]="['/invoicing/invoices', i.id]">{{ 'common.view' | transloco }}</a></td>
            </tr>
          } @empty {
            <tr><td colspan="6" class="empty">{{ (list.loading() ? 'common.loading' : 'invoicing.invoices.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
  `,
})
export class InvoicesPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);

  protected readonly summary = signal<SriInvoicingSummary | null>(null);
  protected readonly summaryError = signal<string | null>(null);
  protected readonly status = signal('');
  protected readonly inProgress = computed(() => countOf(this.summary(), IN_PROGRESS_STATUSES));
  protected readonly tone = invoiceTone;
  protected readonly statuses = [
    'PendingSignature', 'SignedDocument', 'PendingSubmission', 'Received', 'Processing', 'Authorized', 'NotAuthorized', 'Rejected', 'Voided', 'Discarded',
  ] as const;

  protected readonly list = new ListState<SriInvoiceListItem>((q) => this.api.sriInvoices(q), this.errors, inject(DestroyRef));

  ngOnInit(): void {
    this.list.load();
    this.api.sriInvoicingSummary().subscribe({
      next: (s) => this.summary.set(s),
      error: (err: unknown) => this.summaryError.set(this.errors.message(err)),
    });
  }

  protected filter(status: string): void {
    this.status.set(status);
    this.list.set({ status });
  }
}
