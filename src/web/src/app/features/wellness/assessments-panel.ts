import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Assessment, AssessmentComparison, AssessmentPoint } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import type { ChartPoint } from '../../shared/chart-math';
import { FcDate, FcNumber } from '../../shared/format.pipes';
import { toNumber, todayIso } from '../../shared/form-utils';
import { LineChart } from '../../shared/line-chart';
import { ToastService } from '../../shared/toast';

type Metric = 'weightKg' | 'bmi' | 'waistCm' | 'hipCm' | 'chestCm' | 'armCm' | 'thighCm' | 'bodyFatPercent';
const METRICS: readonly Metric[] = ['weightKg', 'bmi', 'waistCm', 'hipCm', 'chestCm', 'armCm', 'thighCm', 'bodyFatPercent'];
const UNITS: Record<Metric, string> = { weightKg: 'kg', bmi: '', waistCm: 'cm', hipCm: 'cm', chestCm: 'cm', armCm: 'cm', thighCm: 'cm', bodyFatPercent: '%' };

/**
 * Evaluaciones físicas: historial, evolución y comparación. Los registros no se editan (un error se corrige con una evaluación
 * nueva). El IMC es un indicador general, no un diagnóstico, y solo se muestra cuando el servidor lo calcula.
 */
@Component({
  selector: 'fc-assessments-panel',
  imports: [TranslocoPipe, Badge, FcDate, FcNumber, LineChart],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    @if (points().length > 0) {
      <div class="subcard">
        <label class="field inline-field">
          <span>{{ 'assessments.metric' | transloco }}</span>
          <select [value]="metric()" (change)="metric.set($any($event.target).value)">
            @for (m of metrics; track m) {
              <option [value]="m">{{ 'assessments.metrics.' + m | transloco }}</option>
            }
          </select>
        </label>
        <fc-line-chart [points]="series()" [unit]="unit()" [label]="'assessments.chartLabel' | transloco: { metric: ('assessments.metrics.' + metric() | transloco) }" />
        <p class="muted"><small>{{ 'assessments.bmiNote' | transloco }}</small></p>
      </div>
    }

    @if (canManage()) {
      <div class="subcard">
        <h3>{{ (correcting() ? 'assessments.correctTitle' : 'assessments.new') | transloco }}</h3>
        @if (correcting()) {
          <p class="alert alert-warning">{{ 'assessments.correctNote' | transloco }} <button type="button" class="btn btn-ghost" (click)="cancelCorrection()">{{ 'common.cancel' | transloco }}</button></p>
        }
        <div class="form-grid">
          <label class="field"><span>{{ 'assessments.date' | transloco }}</span><input type="date" [value]="form().assessedOn" (input)="patch('assessedOn', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'assessments.metrics.weightKg' | transloco }} (kg)</span><input inputmode="decimal" [value]="form().weightKg" (input)="patch('weightKg', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'assessments.heightCm' | transloco }} (cm)</span><input inputmode="decimal" [value]="form().heightCm" (input)="patch('heightCm', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'assessments.metrics.waistCm' | transloco }} (cm)</span><input inputmode="decimal" [value]="form().waistCm" (input)="patch('waistCm', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'assessments.metrics.hipCm' | transloco }} (cm)</span><input inputmode="decimal" [value]="form().hipCm" (input)="patch('hipCm', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'assessments.metrics.chestCm' | transloco }} (cm)</span><input inputmode="decimal" [value]="form().chestCm" (input)="patch('chestCm', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'assessments.metrics.armCm' | transloco }} (cm)</span><input inputmode="decimal" [value]="form().armCm" (input)="patch('armCm', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'assessments.metrics.thighCm' | transloco }} (cm)</span><input inputmode="decimal" [value]="form().thighCm" (input)="patch('thighCm', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'assessments.metrics.bodyFatPercent' | transloco }} (%)</span><input inputmode="decimal" [value]="form().bodyFatPercent" (input)="patch('bodyFatPercent', $any($event.target).value)" /></label>
          @if (form().bodyFatPercent.trim() !== '') {
            <label class="field">
              <span>{{ 'assessments.bodyFatMethod' | transloco }}</span>
              <select [value]="form().bodyFatMethod" (change)="patch('bodyFatMethod', $any($event.target).value)">
                @for (m of fatMethods; track m) {
                  <option [value]="m">{{ 'enums.bodyFatMethod.' + m | transloco }}</option>
                }
              </select>
            </label>
          }
          <label class="field">
            <span>{{ 'assessments.source' | transloco }}</span>
            <select [value]="form().source" (change)="patch('source', $any($event.target).value)">
              @for (s of sources; track s) {
                <option [value]="s">{{ 'enums.measurementSource.' + s | transloco }}</option>
              }
            </select>
          </label>
        </div>
        <label class="field"><span>{{ 'common.notes' | transloco }}</span><textarea maxlength="500" [value]="form().notes" (input)="patch('notes', $any($event.target).value)"></textarea></label>
        <div class="inline-actions">
          <button type="button" class="btn btn-primary" [disabled]="saving()" (click)="save()">{{ 'assessments.save' | transloco }}</button>
        </div>
      </div>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'assessments.date' | transloco }}</th>
            <th class="num">{{ 'assessments.metrics.weightKg' | transloco }}</th>
            <th class="num">{{ 'assessments.heightCm' | transloco }}</th>
            <th class="num">{{ 'assessments.metrics.bmi' | transloco }}</th>
            <th class="num">{{ 'assessments.metrics.waistCm' | transloco }}</th>
            <th class="num">{{ 'assessments.metrics.bodyFatPercent' | transloco }}</th>
            <th>{{ 'assessments.source' | transloco }}</th>
            <th><span class="sr-only">{{ 'common.actions' | transloco }}</span></th>
          </tr>
        </thead>
        <tbody>
          @for (a of items(); track a.id) {
            <tr [class.muted]="a.isCorrected">
              <td>{{ a.assessedOn | fcDate }}</td>
              <td class="num">{{ a.weightKg | fcNumber }}</td>
              <td class="num">{{ a.heightCm | fcNumber }}</td>
              <td class="num">
                {{ a.bmi | fcNumber }}
                @if (a.bmi && a.heightCarried) {
                  <small class="muted" [title]="'assessments.heightCarriedHint' | transloco">*</small>
                }
                @if (a.bmiReferenceRange) {
                  <fc-badge tone="neutral">{{ 'assessments.range.' + a.bmiReferenceRange | transloco }}</fc-badge>
                }
              </td>
              <td class="num">{{ a.waistCm | fcNumber }}</td>
              <td class="num">{{ a.bodyFatPercent | fcNumber }}</td>
              <td>{{ 'enums.measurementSource.' + a.source | transloco }}</td>
              <td>
                @if (a.isCorrected) {
                  <fc-badge tone="neutral">{{ 'assessments.corrected' | transloco }}</fc-badge>
                } @else if (a.correctsId) {
                  <fc-badge tone="info">{{ 'assessments.correction' | transloco }}</fc-badge>
                }
                @if (!a.isCorrected && canManage()) {
                  <button type="button" class="btn btn-ghost" (click)="correct(a)">{{ 'assessments.correctAction' | transloco }}</button>
                }
                @if (!a.isCorrected) {
                  <button type="button" class="btn btn-ghost" [class.active]="picked().includes(a.id)" (click)="toggle(a.id)">
                    {{ picked().includes(a.id) ? ('assessments.unpick' | transloco) : ('assessments.pick' | transloco) }}
                  </button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="8" class="empty">{{ 'assessments.empty' | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <p class="muted"><small>{{ 'assessments.immutableNote' | transloco }}</small></p>

    @if (picked().length === 2) {
      <div class="inline-actions"><button type="button" class="btn" (click)="compare()">{{ 'assessments.compare' | transloco }}</button></div>
    }
    @if (comparison(); as c) {
      <div class="subcard">
        <h3>{{ 'assessments.comparison' | transloco: { days: c.daysBetween } }}</h3>
        <table class="table">
          <thead><tr><th>{{ 'assessments.metric' | transloco }}</th><th class="num">{{ 'assessments.from' | transloco }}</th><th class="num">{{ 'assessments.to' | transloco }}</th><th class="num">{{ 'assessments.delta' | transloco }}</th></tr></thead>
          <tbody>
            @for (ch of c.changes; track ch.metric) {
              <tr>
                <td>{{ 'assessments.metrics.' + ch.metric | transloco }}</td>
                <td class="num">{{ ch.from | fcNumber }} {{ ch.unit }}</td>
                <td class="num">{{ ch.to | fcNumber }} {{ ch.unit }}</td>
                <td class="num">{{ (ch.delta ?? 0) > 0 ? '+' : '' }}{{ ch.delta | fcNumber }} {{ ch.unit }}</td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }
  `,
})
export class AssessmentsPanel implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  readonly memberId = input.required<string>();

  protected readonly metrics = METRICS;
  protected readonly sources = ['Manual', 'Scale', 'Tape', 'Caliper', 'Bioimpedance', 'Other'] as const;
  protected readonly fatMethods = ['Caliper', 'Bioimpedance', 'Dexa', 'Other'] as const;
  protected readonly items = signal<Assessment[]>([]);
  protected readonly points = signal<AssessmentPoint[]>([]);
  protected readonly metric = signal<Metric>('weightKg');
  protected readonly picked = signal<string[]>([]);
  protected readonly comparison = signal<AssessmentComparison | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly correcting = signal<string | null>(null);
  protected readonly canManage = computed(() => this.session.has(Perm.AssessmentsManage));
  protected readonly unit = computed(() => UNITS[this.metric()]);
  protected readonly form = signal(this.emptyForm());

  protected readonly series = computed<ChartPoint[]>(() =>
    this.points().flatMap((p) => {
      const value = p[this.metric()];
      return typeof value === 'number' ? [{ label: p.date, value }] : [];
    }),
  );

  ngOnInit(): void {
    this.reload();
  }

  protected patch(key: string, value: string): void {
    this.form.update((f) => ({ ...f, [key]: value }));
  }

  protected correct(a: Assessment): void {
    this.correcting.set(a.id);
    this.form.set({
      assessedOn: a.assessedOn, weightKg: a.weightKg?.toString() ?? '', heightCm: a.heightCm?.toString() ?? '', waistCm: a.waistCm?.toString() ?? '',
      hipCm: a.hipCm?.toString() ?? '', chestCm: a.chestCm?.toString() ?? '', armCm: a.armCm?.toString() ?? '', thighCm: a.thighCm?.toString() ?? '',
      bodyFatPercent: a.bodyFatPercent?.toString() ?? '', bodyFatMethod: a.bodyFatMethod ?? 'Caliper', source: a.source, notes: '',
    });
  }

  protected cancelCorrection(): void {
    this.correcting.set(null);
    this.form.set(this.emptyForm());
  }

  protected toggle(id: string): void {
    this.comparison.set(null);
    this.picked.update((list) => (list.includes(id) ? list.filter((x) => x !== id) : list.length < 2 ? [...list, id] : [list[1], id]));
  }

  protected compare(): void {
    const [a, b] = this.picked();
    this.api.compareAssessments(this.memberId(), a, b).subscribe({
      next: (c) => this.comparison.set(c),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  protected save(): void {
    const f = this.form();
    this.saving.set(true);
    this.error.set(null);
    const fat = toNumber(f.bodyFatPercent);
    this.api
      .createAssessment(this.memberId(), {
        assessedOn: f.assessedOn,
        weightKg: toNumber(f.weightKg),
        heightCm: toNumber(f.heightCm),
        waistCm: toNumber(f.waistCm),
        hipCm: toNumber(f.hipCm),
        chestCm: toNumber(f.chestCm),
        armCm: toNumber(f.armCm),
        thighCm: toNumber(f.thighCm),
        bodyFatPercent: fat,
        bodyFatMethod: fat === null ? null : (f.bodyFatMethod as 'Caliper'),
        source: f.source as 'Manual',
        notes: f.notes.trim() || null,
        correctsId: this.correcting(),
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.form.set(this.emptyForm());
          this.correcting.set(null);
          this.toast.success('assessments.savedToast');
          this.reload();
        },
        error: (err: unknown) => {
          this.saving.set(false);
          this.error.set(this.errors.message(err));
        },
      });
  }

  private reload(): void {
    this.api.assessments(this.memberId(), { pageSize: 100 }).subscribe({
      next: (page) => this.items.set(page.items),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
    this.api.assessmentProgress(this.memberId()).subscribe({
      next: (points) => this.points.set(points),
      error: () => this.points.set([]),
    });
  }

  private emptyForm() {
    return {
      assessedOn: todayIso(), weightKg: '', heightCm: '', waistCm: '', hipCm: '', chestCm: '', armCm: '', thighCm: '',
      bodyFatPercent: '', bodyFatMethod: 'Caliper', source: 'Manual', notes: '',
    };
  }
}
