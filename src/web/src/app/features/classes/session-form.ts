import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { CreateSeriesRequest, GroupClass, SeriesRun, Trainer } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { BranchCatalog } from '../../shared/branch-catalog';
import { Modal } from '../../shared/modal';
import { ToastService } from '../../shared/toast';
import { DEFAULT_TIME_ZONE, toInstant } from '../../shared/zoned-time';

type Mode = 'single' | 'series';

const WEEK: readonly string[] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

/**
 * Programar una sesión suelta o una serie semanal. La fecha y la hora se escriben en la hora de la sucursal de la clase (no en la del
 * navegador). Una serie genera sesiones concretas: el resultado dice cuántas se crearon y qué fechas se omitieron, y por qué.
 */
@Component({
  selector: 'fc-session-form',
  imports: [ReactiveFormsModule, TranslocoPipe, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="'schedule.newTitle' | transloco" (closed)="closed.emit()">
      @if (run(); as result) {
        <section aria-live="polite">
          <p class="alert alert-success">{{ 'schedule.seriesDone' | transloco: { created: result.created, existed: result.alreadyExisted } }}</p>
          @if (result.pastDates > 0) {
            <p class="muted">{{ 'schedule.seriesPast' | transloco: { count: result.pastDates } }}</p>
          }
          @if (result.skipped.length > 0) {
            <h3>{{ 'schedule.skippedTitle' | transloco }}</h3>
            <ul>
              @for (s of result.skipped; track s.date + s.reason) {
                <li>{{ format.dateOnly(s.date) }} · {{ 'schedule.skipped.' + s.reason | transloco }}</li>
              }
            </ul>
          }
        </section>
      } @else {
        <form id="session-form" [formGroup]="form" (ngSubmit)="submit()" novalidate>
          @if (error()) {
            <p class="alert alert-danger" role="alert">{{ error() }}</p>
          }
          <div class="tabs" role="tablist">
            <button type="button" role="tab" class="tab" [class.active]="mode() === 'single'" [attr.aria-selected]="mode() === 'single'" (click)="mode.set('single')">
              {{ 'schedule.single' | transloco }}
            </button>
            <button type="button" role="tab" class="tab" [class.active]="mode() === 'series'" [attr.aria-selected]="mode() === 'series'" (click)="mode.set('series')">
              {{ 'schedule.series' | transloco }}
            </button>
          </div>

          <label class="field">
            <span>{{ 'schedule.class' | transloco }}</span>
            <select formControlName="classId" (change)="onClassChange()">
              <option value="">—</option>
              @for (c of classes(); track c.id) {
                <option [value]="c.id">{{ c.name }} · {{ c.branchName }}</option>
              }
            </select>
          </label>

          @if (mode() === 'single') {
            <div class="grid-2">
              <label class="field">
                <span>{{ 'common.date' | transloco }}</span>
                <input type="date" formControlName="date" />
              </label>
              <label class="field">
                <span>{{ 'schedule.time' | transloco }}</span>
                <input type="time" formControlName="time" />
                <small class="muted">{{ 'schedule.branchTime' | transloco: { zone: timeZone() } }}</small>
              </label>
            </div>
          } @else {
            <div class="grid-2">
              <label class="field">
                <span>{{ 'schedule.startDate' | transloco }}</span>
                <input type="date" formControlName="date" />
              </label>
              <label class="field">
                <span>{{ 'schedule.endDate' | transloco }}</span>
                <input type="date" formControlName="endDate" />
              </label>
              <label class="field">
                <span>{{ 'schedule.time' | transloco }}</span>
                <input type="time" formControlName="time" />
                <small class="muted">{{ 'schedule.branchTime' | transloco: { zone: timeZone() } }}</small>
              </label>
            </div>
            <fieldset class="field">
              <legend>{{ 'schedule.days' | transloco }}</legend>
              <div class="day-picker">
                @for (day of week; track day) {
                  <label class="check">
                    <input type="checkbox" [checked]="days().has(day)" (change)="toggleDay(day, $any($event.target).checked)" />
                    <span>{{ 'enums.dayOfWeek.' + day | transloco }}</span>
                  </label>
                }
              </div>
              <small class="muted">{{ 'schedule.seriesHint' | transloco }}</small>
            </fieldset>
          }

          <div class="grid-2">
            <label class="field">
              <span>{{ 'schedule.instructorOverride' | transloco }}</span>
              <select formControlName="instructorId">
                <option value="">{{ 'schedule.classInstructor' | transloco }}</option>
                @for (t of instructors(); track t.id) {
                  <option [value]="t.id">{{ t.fullName }}</option>
                }
              </select>
            </label>
            <label class="field">
              <span>{{ 'schedule.capacityOverride' | transloco }}</span>
              <input type="number" formControlName="capacity" min="1" max="500" [placeholder]="selectedClass()?.capacity?.toString() ?? ''" />
            </label>
          </div>

          @if (mode() === 'single') {
            <label class="field">
              <span>{{ 'common.notes' | transloco }}</span>
              <input formControlName="notes" maxlength="500" />
            </label>
            <label class="field">
              <span>{{ 'schedule.overrideReason' | transloco }}</span>
              <input formControlName="overrideReason" maxlength="300" />
              <small class="muted">{{ 'schedule.overrideHint' | transloco }}</small>
            </label>
          }
        </form>
      }
      <ng-container footer>
        @if (run()) {
          <button type="button" class="btn btn-primary" (click)="closed.emit()">{{ 'common.close' | transloco }}</button>
        } @else {
          <button type="button" class="btn" (click)="closed.emit()">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="session-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
        }
      </ng-container>
    </fc-modal>
  `,
})
export class SessionForm implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);
  private readonly catalog = inject(BranchCatalog);
  protected readonly format = inject(Formatter);

  /** Día (`YYYY-MM-DD`) con que se abre el formulario. */
  readonly initialDate = input.required<string>();
  readonly branchId = input<string>('');
  readonly saved = output<void>();
  readonly closed = output<void>();

  protected readonly week = WEEK;
  protected readonly mode = signal<Mode>('single');
  protected readonly classes = signal<GroupClass[]>([]);
  protected readonly instructors = signal<Trainer[]>([]);
  protected readonly days = signal<Set<string>>(new Set(['Monday', 'Wednesday', 'Friday']));
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly run = signal<SeriesRun | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    classId: ['', [Validators.required]],
    date: ['', [Validators.required]],
    endDate: [''],
    time: ['09:00', [Validators.required]],
    instructorId: [''],
    capacity: [null as number | null],
    notes: [''],
    overrideReason: [''],
  });

  private readonly classId = signal('');
  protected readonly selectedClass = computed(() => this.classes().find((c) => c.id === this.classId()) ?? null);
  protected readonly timeZone = computed(() => {
    const branchId = this.selectedClass()?.branchId ?? this.branchId();
    return this.catalog.branches().find((b) => b.id === branchId)?.timeZone ?? DEFAULT_TIME_ZONE;
  });

  ngOnInit(): void {
    this.catalog.ensure();
    this.form.controls.date.setValue(this.initialDate());
    this.form.controls.endDate.setValue(this.initialDate());
    this.api.groupClasses({ pageSize: 100, isActive: true, branchId: this.branchId() }).subscribe({
      next: (page) => this.classes.set(page.items),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  protected onClassChange(): void {
    this.classId.set(this.form.controls.classId.value);
    this.form.controls.instructorId.setValue('');
    this.instructors.set([]);
    const branchId = this.selectedClass()?.branchId;
    if (branchId) {
      this.api.trainers({ pageSize: 100, status: 'Active', branchId }).subscribe((page) => this.instructors.set(page.items));
    }
  }

  protected toggleDay(day: string, checked: boolean): void {
    this.days.update((set) => {
      const next = new Set(set);
      if (checked) {
        next.add(day);
      } else {
        next.delete(day);
      }
      return next;
    });
  }

  protected submit(): void {
    const v = this.form.getRawValue();
    const series = this.mode() === 'series';
    if (!v.classId || !v.date || !v.time || (series && (!v.endDate || this.days().size === 0))) {
      this.form.markAllAsTouched();
      this.error.set(this.errors.text(series && this.days().size === 0 ? 'schedule.daysRequired' : 'schedule.invalidForm'));
      return;
    }
    const capacity = v.capacity === null || String(v.capacity) === '' ? null : Number(v.capacity);
    this.saving.set(true);
    this.error.set(null);
    if (series) {
      this.api
        .createClassSeries({
          classId: v.classId,
          startDate: v.date,
          endDate: v.endDate,
          startTime: v.time,
          days: WEEK.filter((d) => this.days().has(d)) as CreateSeriesRequest['days'],
          instructorId: v.instructorId || null,
          capacity,
        })
        .subscribe({
          next: (result) => {
            this.saving.set(false);
            this.run.set(result);
            this.saved.emit();
          },
          error: (err: unknown) => this.fail(err),
        });
      return;
    }
    this.api
      .createClassSession({
        classId: v.classId,
        startsAt: toInstant(v.date, v.time, this.timeZone()),
        instructorId: v.instructorId || null,
        capacity,
        notes: v.notes.trim() || null,
        availabilityOverrideReason: v.overrideReason.trim() || null,
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.toast.success('schedule.createdToast');
          this.saved.emit();
          this.closed.emit();
        },
        error: (err: unknown) => this.fail(err),
      });
  }

  private fail(err: unknown): void {
    this.saving.set(false);
    this.error.set(this.errors.message(err));
  }
}
