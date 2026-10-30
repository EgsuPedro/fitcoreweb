import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { ContactRequestItem } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

/**
 * Solicitudes de contacto y demostración recibidas por el sitio público. Solo el equipo de plataforma las ve y las marca como atendidas. Se eliminan solas a los 180 días.
 */
@Component({
  selector: 'fc-platform-leads',
  imports: [DatePipe, TranslocoPipe, Badge, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'platform.leads.title' | transloco }}</h1>
        <p class="muted">{{ 'platform.leads.subtitle' | transloco }}</p>
      </div>
    </header>

    <section class="card filters">
      <div class="filters-row">
        <label class="inline-field">
          <span>{{ 'common.status' | transloco }}</span>
          <select (change)="setFilter('status', $any($event.target).value)">
            <option value="">{{ 'common.all' | transloco }}</option>
            <option value="New">{{ 'platform.leads.status.New' | transloco }}</option>
            <option value="Attended">{{ 'platform.leads.status.Attended' | transloco }}</option>
          </select>
        </label>
        <label class="inline-field">
          <span>{{ 'platform.leads.kind' | transloco }}</span>
          <select (change)="setFilter('kind', $any($event.target).value)">
            <option value="">{{ 'common.all' | transloco }}</option>
            <option value="Contact">{{ 'platform.leads.kinds.Contact' | transloco }}</option>
            <option value="Demo">{{ 'platform.leads.kinds.Demo' | transloco }}</option>
          </select>
        </label>
      </div>
    </section>

    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    <div class="card table-card">
      <table class="table">
        <caption class="sr-only">{{ 'platform.leads.title' | transloco }}</caption>
        <thead>
          <tr>
            <th scope="col">{{ 'platform.leads.received' | transloco }}</th>
            <th scope="col">{{ 'platform.leads.who' | transloco }}</th>
            <th scope="col">{{ 'platform.leads.kind' | transloco }}</th>
            <th scope="col">{{ 'platform.leads.message' | transloco }}</th>
            <th scope="col">{{ 'common.status' | transloco }}</th>
            <th scope="col"></th>
          </tr>
        </thead>
        <tbody>
          @for (item of items(); track item.id) {
            <tr>
              <td>{{ item.createdAt | date: 'short' }}</td>
              <th scope="row">
                {{ item.name }}<br />
                <a [href]="'mailto:' + item.email">{{ item.email }}</a>
                @if (item.phone) { <br /><small class="muted">{{ item.phone }}</small> }
                @if (item.gymName) { <br /><small class="muted">{{ item.gymName }}</small> }
              </th>
              <td>{{ 'platform.leads.kinds.' + item.kind | transloco }}</td>
              <td class="lead-message">{{ item.message }}</td>
              <td><fc-badge [tone]="item.status === 'Attended' ? 'ok' : 'warn'">{{ 'platform.leads.status.' + item.status | transloco }}</fc-badge></td>
              <td class="row-actions">
                @if (canManage()) {
                  <button type="button" class="btn" [disabled]="busy()" (click)="toggle(item)">
                    {{ (item.status === 'Attended' ? 'platform.leads.reopen' : 'platform.leads.attend') | transloco }}
                  </button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="6" class="empty">{{ (loading() ? 'common.loading' : 'platform.leads.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="page()" [pageSize]="pageSize" [total]="total()" (pageChange)="goTo($event)" />
  `,
})
export class PlatformLeads implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  protected readonly pageSize = 25;
  protected readonly items = signal<ContactRequestItem[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly canManage = computed(() => this.session.has(Perm.PlatformLeadsManage));
  private filters: { status?: string; kind?: string } = {};

  ngOnInit(): void {
    this.load();
  }

  protected setFilter(name: 'status' | 'kind', value: string): void {
    this.filters = { ...this.filters, [name]: value || undefined };
    this.page.set(1);
    this.load();
  }

  protected goTo(page: number): void {
    this.page.set(page);
    this.load();
  }

  protected toggle(item: ContactRequestItem): void {
    this.busy.set(true);
    this.api.setContactRequestAttended(item.id, item.status !== 'Attended').subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success(item.status === 'Attended' ? 'platform.leads.reopened' : 'platform.leads.attended');
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.api.contactRequests({ ...this.filters, page: this.page(), pageSize: this.pageSize }).subscribe({
      next: (r) => {
        this.items.set(r.items);
        this.total.set(r.totalCount);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
