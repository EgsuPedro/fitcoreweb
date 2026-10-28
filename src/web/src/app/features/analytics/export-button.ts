import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { switchMap, tap } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { ExportReportKey, ExportRequest, PeriodPresetKey } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { ToastService } from '../../shared/toast';
import { ReportFilters } from './report-filters';

/** Guarda un archivo que llegó como blob, sin enlaces públicos: la descarga ya se hizo con la sesión del usuario. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}

/**
 * «Exportar CSV» de un reporte: pide la exportación con los mismos filtros que se ven en pantalla y descarga el archivo. Solo aparece a quien tiene el
 * permiso de exportar; el servidor vuelve a comprobarlo (y el del reporte) al pedir y al descargar.
 */
@Component({
  selector: 'fc-export-button',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (allowed()) {
      <span class="export-box">
        <button type="button" class="btn" [disabled]="busy() || !filters.query()" (click)="run()">
          {{ (busy() ? 'analytics.export.working' : 'analytics.export.button') | transloco }}
        </button>
        @if (error()) {
          <span class="alert alert-danger" role="alert">{{ error() }}</span>
        }
      </span>
    }
  `,
})
export class ExportButton {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  protected readonly filters = inject(ReportFilters);

  readonly report = input.required<ExportReportKey>();
  /** Filtros propios del reporte (plan, estado, medio, vista…) que se suman a los del período y las sucursales. */
  readonly extra = input<Record<string, string | number | boolean | null | undefined>>({});

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected allowed(): boolean {
    return this.session.has(Perm.ReportsExport);
  }

  protected run(): void {
    const query = this.filters.query();
    if (!query) {
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    const body = {
      report: this.report(),
      preset: this.filters.preset() as PeriodPresetKey,
      from: this.filters.preset() === 'Custom' ? this.filters.from() : null,
      to: this.filters.preset() === 'Custom' ? this.filters.to() : null,
      branchIds: this.filters.branchIds().length > 0 ? this.filters.branchIds() : null,
      planId: null,
      method: null,
      overdueOnly: null,
      days: null,
      hideRenewed: null,
      onlyWithDifference: null,
      activity: null,
      classId: null,
      ...(this.extra() as Partial<ExportRequest>),
    } as ExportRequest;
    this.api
      .createExport(body)
      .pipe(
        tap((created) => this.toast.success('analytics.export.created', { rows: created.rowCount })),
        switchMap((created) => this.api.downloadExport(created.id).pipe(tap((blob) => saveBlob(blob, created.fileName)))),
      )
      .subscribe({
        next: () => this.busy.set(false),
        error: (err: unknown) => {
          this.busy.set(false);
          this.error.set(this.errors.message(err));
        },
      });
  }
}
