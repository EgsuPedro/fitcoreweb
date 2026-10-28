import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { ReportExport } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { saveBlob } from './export-button';

/**
 * Historial de las exportaciones propias. Cada archivo vence a las 24 horas; solo quien lo pidió lo ve y lo baja, con su sesión (no hay enlaces
 * públicos). Un archivo vencido queda en el historial como «Purgado»: se conserva quién, cuándo y qué se exportó, pero no el contenido.
 */
@Component({
  selector: 'fc-analytics-exports',
  imports: [TranslocoPipe, Badge, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'analytics.exports.title' | transloco }}</h1>
        <p class="muted">{{ 'analytics.exports.subtitle' | transloco }}</p>
      </div>
    </header>

    @if (list.error()) { <p class="alert alert-danger" role="alert">{{ list.error() }}</p> }
    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'analytics.exports.report' | transloco }}</th>
            <th>{{ 'analytics.exports.requestedAt' | transloco }}</th>
            <th class="num">{{ 'analytics.exports.rows' | transloco }}</th>
            <th>{{ 'analytics.exports.expiresAt' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th class="num">{{ 'analytics.exports.downloads' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (e of list.items(); track e.id) {
            <tr>
              <td>{{ 'analytics.exports.reports.' + e.report | transloco }}<br /><small class="muted">{{ e.fileName }}</small></td>
              <td>{{ dateTime(e.requestedAt) }}</td>
              <td class="num">{{ e.rowCount }}</td>
              <td>{{ dateTime(e.expiresAt) }}</td>
              <td><fc-badge [tone]="tone(e.status)">{{ 'analytics.exports.statuses.' + e.status | transloco }}</fc-badge></td>
              <td class="num">{{ e.downloadCount }}</td>
              <td class="row-actions">
                @if (e.status === 'Available') {
                  <button type="button" class="btn" [disabled]="busyId() === e.id" (click)="download(e)">{{ 'analytics.exports.download' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="7" class="empty">{{ (list.loading() ? 'common.loading' : 'analytics.exports.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
    <p class="muted">{{ 'analytics.exports.policy' | transloco }}</p>
  `,
})
export class AnalyticsExports implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly format = inject(Formatter);

  protected readonly list = new ListState<ReportExport>((q) => this.api.exports(q), this.errors, inject(DestroyRef));
  protected readonly busyId = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.list.load();
  }

  protected dateTime(iso: string): string {
    return this.format.dateTime(iso);
  }

  protected tone(status: string): 'ok' | 'warn' | 'off' {
    return status === 'Available' ? 'ok' : status === 'Expired' ? 'warn' : 'off';
  }

  protected download(export_: ReportExport): void {
    this.busyId.set(export_.id);
    this.error.set(null);
    this.api.downloadExport(export_.id).subscribe({
      next: (blob) => {
        saveBlob(blob, export_.fileName);
        this.busyId.set(null);
        this.list.load();
      },
      error: (err: unknown) => {
        this.busyId.set(null);
        this.error.set(this.errors.message(err));
        this.list.load();
      },
    });
  }
}
