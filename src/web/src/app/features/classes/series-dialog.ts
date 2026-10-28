import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { ClassSeries } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { Modal } from '../../shared/modal';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';

/** Series semanales: cuántas sesiones tienen, extenderlas (sin duplicar nada) o cancelarlas con sus sesiones futuras. */
@Component({
  selector: 'fc-series-dialog',
  imports: [TranslocoPipe, Badge, Modal, ReasonDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="'schedule.seriesTitle' | transloco" (closed)="closed.emit()">
      @if (error()) {
        <p class="alert alert-danger" role="alert">{{ error() }}</p>
      }
      @if (notice()) {
        <p class="alert alert-success" role="status">{{ notice() }}</p>
      }
      <div class="card table-card">
        <table class="table">
          <thead>
            <tr>
              <th>{{ 'schedule.class' | transloco }}</th>
              <th>{{ 'schedule.days' | transloco }}</th>
              <th>{{ 'schedule.range' | transloco }}</th>
              <th class="num">{{ 'schedule.sessions' | transloco }}</th>
              <th>{{ 'common.status' | transloco }}</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            @for (s of items(); track s.id) {
              <tr>
                <td>{{ s.className }}<br /><small class="muted">{{ s.instructorName }} · {{ s.startTime.slice(0, 5) }}</small></td>
                <td>
                  @for (d of s.days; track d) {
                    <span class="badge">{{ 'enums.dayOfWeek.' + d | transloco }}</span>
                  }
                </td>
                <td>{{ format.dateOnly(s.startDate) }} → {{ format.dateOnly(s.endDate) }}</td>
                <td class="num">{{ s.scheduledSessions }} / {{ s.totalSessions }}</td>
                <td><fc-badge [tone]="s.status === 'Active' ? 'ok' : 'off'">{{ 'enums.seriesStatus.' + s.status | transloco }}</fc-badge></td>
                <td class="actions">
                  @if (canManage() && s.status === 'Active') {
                    <button type="button" class="btn" (click)="extending.set(s); extendTo.set(s.endDate)">{{ 'schedule.extend' | transloco }}</button>
                    <button type="button" class="btn btn-danger" (click)="cancelling.set(s)">{{ 'schedule.cancelSeries' | transloco }}</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="empty">{{ (loading() ? 'common.loading' : 'schedule.noSeries') | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>
      <ng-container footer>
        <button type="button" class="btn" (click)="closed.emit()">{{ 'common.close' | transloco }}</button>
      </ng-container>
    </fc-modal>

    @if (extending(); as series) {
      <fc-modal [title]="'schedule.extend' | transloco" (closed)="extending.set(null)">
        <p class="muted">{{ 'schedule.extendHint' | transloco }}</p>
        <label class="field">
          <span>{{ 'schedule.endDate' | transloco }}</span>
          <input type="date" [min]="series.endDate" [value]="extendTo()" (input)="extendTo.set($any($event.target).value)" />
        </label>
        <ng-container footer>
          <button type="button" class="btn" (click)="extending.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="extend(series)">{{ 'schedule.extend' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (cancelling(); as series) {
      <fc-reason-dialog
        [title]="'schedule.cancelSeries' | transloco"
        [label]="'common.reason' | transloco"
        [message]="'schedule.cancelSeriesWarning' | transloco: { count: series.scheduledSessions }"
        [confirmLabel]="'schedule.cancelSeries' | transloco"
        [minLength]="3"
        [danger]="true"
        [busy]="busy()"
        [error]="dialogError()"
        (submitted)="cancelSeries(series, $event)"
        (cancelled)="cancelling.set(null)"
      />
    }
  `,
})
export class SeriesDialog implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  protected readonly format = inject(Formatter);

  readonly branchId = input<string>('');
  readonly changed = output<void>();
  readonly closed = output<void>();

  protected readonly items = signal<ClassSeries[]>([]);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly extending = signal<ClassSeries | null>(null);
  protected readonly cancelling = signal<ClassSeries | null>(null);
  protected readonly extendTo = signal('');
  protected readonly canManage = computed(() => this.session.has(Perm.ClassSessionsManage));

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.api.classSeriesList({ pageSize: 100, branchId: this.branchId() }).subscribe({
      next: (page) => {
        this.items.set(page.items);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected extend(series: ClassSeries): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.generateClassSeries(series.id, this.extendTo() && this.extendTo() !== series.endDate ? this.extendTo() : null).subscribe({
      next: (run) => {
        this.busy.set(false);
        this.extending.set(null);
        this.notice.set(this.errors.text('schedule.extendedNotice', { created: run.created, existed: run.alreadyExisted, skipped: run.skipped.length }));
        this.load();
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.extending.set(null);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected cancelSeries(series: ClassSeries, reason: string): void {
    this.busy.set(true);
    this.dialogError.set(null);
    this.api.cancelClassSeries(series.id, reason).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.cancelling.set(null);
        this.toast.success('schedule.seriesCancelledToast');
        this.notice.set(this.errors.text('schedule.seriesCancelledNotice', { count: result.cancelledSessions }));
        this.load();
        this.changed.emit();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.dialogError.set(this.errors.message(err));
      },
    });
  }
}
