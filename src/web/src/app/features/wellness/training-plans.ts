import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Exercise, TrainingPlanDetail, TrainingPlanRequest, TrainingPlanSummary, TrainingPlanVersion } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDate } from '../../shared/format.pipes';
import { toInt, toNumber } from '../../shared/form-utils';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

interface ExerciseDraft {
  exerciseId: string;
  name: string;
  trackingMode: string;
  sets: string;
  reps: string;
  seconds: string;
  rest: string;
  load: string;
  notes: string;
}

interface DayDraft {
  name: string;
  notes: string;
  exercises: ExerciseDraft[];
}

interface PlanDraft {
  id: string | null;
  name: string;
  goal: string;
  description: string;
  level: string;
  estimatedMinutes: string;
  validFrom: string;
  validUntil: string;
  changeNote: string;
  days: DayDraft[];
}

const LEVELS = ['Beginner', 'Intermediate', 'Advanced'] as const;

function move<T>(list: T[], index: number, delta: number): T[] {
  const target = index + delta;
  if (target < 0 || target >= list.length) {
    return list;
  }
  const copy = [...list];
  [copy[index], copy[target]] = [copy[target], copy[index]];
  return copy;
}

/**
 * Constructor de rutinas. Guardar una rutina que ya existe crea una versión nueva: las asignaciones en curso conservan la versión
 * con la que se asignaron, así que editar nunca cambia lo que un alumno ya está siguiendo.
 */
@Component({
  selector: 'fc-training-plans',
  imports: [TranslocoPipe, Badge, FcDate, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'trainingPlans.title' | transloco }}</h1>
      @if (canManage() && !draft()) {
        <button type="button" class="btn btn-primary" (click)="startNew()">{{ 'trainingPlans.new' | transloco }}</button>
      }
    </header>

    @if (list.error()) {
      <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
    }
    @if (notice()) {
      <p class="alert alert-danger" role="alert">{{ notice() }}</p>
    }

    @if (draft(); as d) {
      <form class="card section" (submit)="$event.preventDefault(); save()">
        <h2>{{ (d.id ? 'trainingPlans.edit' : 'trainingPlans.new') | transloco }}</h2>
        @if (d.id) {
          <p class="muted">{{ 'trainingPlans.versionNote' | transloco }}</p>
        }
        <div class="form-grid">
          <label class="field"><span>{{ 'trainingPlans.name' | transloco }}</span><input maxlength="120" [value]="d.name" (input)="patch('name', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'trainingPlans.goal' | transloco }}</span><input maxlength="200" [value]="d.goal" (input)="patch('goal', $any($event.target).value)" /></label>
          <label class="field">
            <span>{{ 'exercises.difficulty' | transloco }}</span>
            <select [value]="d.level" (change)="patch('level', $any($event.target).value)">
              @for (l of levels; track l) {
                <option [value]="l">{{ 'enums.difficulty.' + l | transloco }}</option>
              }
            </select>
          </label>
          <label class="field"><span>{{ 'trainingPlans.minutes' | transloco }}</span><input inputmode="numeric" [value]="d.estimatedMinutes" (input)="patch('estimatedMinutes', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'trainingPlans.validFrom' | transloco }}</span><input type="date" [value]="d.validFrom" (input)="patch('validFrom', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'trainingPlans.validUntil' | transloco }}</span><input type="date" [value]="d.validUntil" (input)="patch('validUntil', $any($event.target).value)" /></label>
        </div>
        <label class="field"><span>{{ 'exercises.description' | transloco }}</span><textarea maxlength="1000" [value]="d.description" (input)="patch('description', $any($event.target).value)"></textarea></label>
        @if (d.id) {
          <label class="field"><span>{{ 'trainingPlans.changeNote' | transloco }}</span><input maxlength="300" [value]="d.changeNote" (input)="patch('changeNote', $any($event.target).value)" /></label>
        }

        <label class="field">
          <span>{{ 'trainingPlans.librarySearch' | transloco }}</span>
          <input type="search" [attr.aria-label]="'trainingPlans.librarySearch' | transloco" (input)="searchLibrary($any($event.target).value)" />
          <small class="muted">{{ 'trainingPlans.libraryHint' | transloco: { shown: library().length } }}</small>
        </label>
        <datalist id="exercise-options">
          @for (e of library(); track e.id) {
            <option [value]="e.name"></option>
          }
        </datalist>

        @for (day of d.days; track $index; let i = $index) {
          <fieldset class="field subcard">
            <legend>{{ 'trainingPlans.day' | transloco: { n: i + 1 } }}</legend>
            <div class="row-form">
              <label class="field"><span>{{ 'trainingPlans.dayName' | transloco }}</span><input maxlength="80" [value]="day.name" (input)="patchDay(i, 'name', $any($event.target).value)" /></label>
              <label class="field"><span>{{ 'common.notes' | transloco }}</span><input maxlength="300" [value]="day.notes" (input)="patchDay(i, 'notes', $any($event.target).value)" /></label>
              <span class="inline-actions">
                <button type="button" class="btn btn-ghost" [disabled]="i === 0" [attr.aria-label]="'trainingPlans.moveUp' | transloco" (click)="moveDay(i, -1)">↑</button>
                <button type="button" class="btn btn-ghost" [disabled]="i === d.days.length - 1" [attr.aria-label]="'trainingPlans.moveDown' | transloco" (click)="moveDay(i, 1)">↓</button>
                <button type="button" class="btn btn-ghost" (click)="removeDay(i)">{{ 'trainingPlans.removeDay' | transloco }}</button>
              </span>
            </div>

            @for (ex of day.exercises; track $index; let j = $index) {
              <div class="row-form">
                <label class="field">
                  <span>{{ 'training.exercise' | transloco }}</span>
                  <input list="exercise-options" [value]="ex.name" (change)="chooseExercise(i, j, $any($event.target).value)" />
                </label>
                <label class="field"><span>{{ 'training.sets' | transloco }}</span><input inputmode="numeric" [value]="ex.sets" (input)="patchExercise(i, j, 'sets', $any($event.target).value)" /></label>
                @if (ex.trackingMode === 'Duration') {
                  <label class="field"><span>{{ 'sessions.seconds' | transloco }}</span><input inputmode="numeric" [value]="ex.seconds" (input)="patchExercise(i, j, 'seconds', $any($event.target).value)" /></label>
                } @else {
                  <label class="field"><span>{{ 'sessions.reps' | transloco }}</span><input inputmode="numeric" [value]="ex.reps" (input)="patchExercise(i, j, 'reps', $any($event.target).value)" /></label>
                }
                @if (ex.trackingMode === 'RepsAndLoad') {
                  <label class="field"><span>{{ 'trainingPlans.load' | transloco }}</span><input inputmode="decimal" [value]="ex.load" (input)="patchExercise(i, j, 'load', $any($event.target).value)" /></label>
                }
                <label class="field"><span>{{ 'training.rest' | transloco }} (s)</span><input inputmode="numeric" [value]="ex.rest" (input)="patchExercise(i, j, 'rest', $any($event.target).value)" /></label>
                <label class="field"><span>{{ 'common.notes' | transloco }}</span><input maxlength="300" [value]="ex.notes" (input)="patchExercise(i, j, 'notes', $any($event.target).value)" /></label>
                <span class="inline-actions">
                  <button type="button" class="btn btn-ghost" [disabled]="j === 0" [attr.aria-label]="'trainingPlans.moveUp' | transloco" (click)="moveExercise(i, j, -1)">↑</button>
                  <button type="button" class="btn btn-ghost" [disabled]="j === day.exercises.length - 1" [attr.aria-label]="'trainingPlans.moveDown' | transloco" (click)="moveExercise(i, j, 1)">↓</button>
                  <button type="button" class="btn btn-ghost" [attr.aria-label]="'common.delete' | transloco" (click)="removeExercise(i, j)">×</button>
                </span>
              </div>
            }
            <button type="button" class="btn" (click)="addExercise(i)">{{ 'trainingPlans.addExercise' | transloco }}</button>
          </fieldset>
        }
        <div class="inline-actions">
          <button type="button" class="btn" (click)="addDay()">{{ 'trainingPlans.addDay' | transloco }}</button>
        </div>

        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <div class="inline-actions">
          <button type="button" class="btn" (click)="draft.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'common.save' | transloco }}</button>
        </div>

        @if (versions().length > 0) {
          <details>
            <summary>{{ 'trainingPlans.versions' | transloco }}</summary>
            <ul class="timeline">
              @for (v of versions(); track v.id) {
                <li><strong>v{{ v.number }}</strong> <span class="muted">{{ v.createdAt | fcDate }}@if (v.changeNote) { · {{ v.changeNote }} }</span></li>
              }
            </ul>
          </details>
        }
      </form>
    } @else {
      <div class="toolbar">
        <input type="search" class="search" [placeholder]="'common.search' | transloco" [attr.aria-label]="'common.search' | transloco" (input)="list.search($any($event.target).value)" />
        <select [attr.aria-label]="'common.status' | transloco" (change)="list.set({ status: $any($event.target).value })">
          <option value="">{{ 'common.all' | transloco }}</option>
          <option value="Active">{{ 'enums.planStatus.Active' | transloco }}</option>
          <option value="Archived">{{ 'enums.planStatus.Archived' | transloco }}</option>
        </select>
      </div>
      <div class="card table-card">
        <table class="table">
          <thead>
            <tr>
              <th>{{ 'trainingPlans.name' | transloco }}</th>
              <th>{{ 'exercises.difficulty' | transloco }}</th>
              <th class="num">{{ 'trainingPlans.days' | transloco }}</th>
              <th class="num">{{ 'trainingPlans.exercises' | transloco }}</th>
              <th class="num">{{ 'trainingPlans.assigned' | transloco }}</th>
              <th>{{ 'common.status' | transloco }}</th>
              <th><span class="sr-only">{{ 'common.actions' | transloco }}</span></th>
            </tr>
          </thead>
          <tbody>
            @for (p of list.items(); track p.id) {
              <tr>
                <td>{{ p.name }} <small class="muted">v{{ p.version }}</small></td>
                <td>{{ 'enums.difficulty.' + p.level | transloco }}</td>
                <td class="num">{{ p.dayCount }}</td>
                <td class="num">{{ p.exerciseCount }}</td>
                <td class="num">{{ p.activeAssignments }}</td>
                <td><fc-badge [tone]="p.status === 'Active' ? 'ok' : 'off'">{{ 'enums.planStatus.' + p.status | transloco }}</fc-badge></td>
                <td class="actions">
                  <button type="button" class="btn btn-ghost" (click)="open(p)">{{ (canManage() ? 'common.edit' : 'common.view') | transloco }}</button>
                  @if (canManage()) {
                    <button type="button" class="btn btn-ghost" (click)="duplicate(p)">{{ 'trainingPlans.duplicate' | transloco }}</button>
                    <button type="button" class="btn btn-ghost" (click)="toggle(p)">{{ (p.status === 'Active' ? 'trainingPlans.archive' : 'trainingPlans.activate') | transloco }}</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="7" class="empty">{{ 'trainingPlans.empty' | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>
      <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
    }
  `,
})
export class TrainingPlans implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  protected readonly levels = LEVELS;
  protected readonly list = new ListState<TrainingPlanSummary>((query) => this.api.trainingPlans(query), this.errors, inject(DestroyRef));
  protected readonly draft = signal<PlanDraft | null>(null);
  protected readonly library = signal<Exercise[]>([]);
  protected readonly versions = signal<TrainingPlanVersion[]>([]);
  protected readonly notice = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly canManage = computed(() => this.session.has(Perm.TrainingPlansManage));

  ngOnInit(): void {
    this.list.load();
    this.searchLibrary('');
  }

  protected searchLibrary(text: string): void {
    this.api.exercises({ search: text, isActive: true, pageSize: 100 }).subscribe({
      next: (page) => this.library.set(page.items),
      error: (err: unknown) => this.notice.set(this.errors.message(err)),
    });
  }

  protected startNew(): void {
    this.formError.set(null);
    this.versions.set([]);
    this.draft.set({
      id: null, name: '', goal: '', description: '', level: 'Beginner', estimatedMinutes: '60', validFrom: '', validUntil: '', changeNote: '',
      days: [{ name: '', notes: '', exercises: [] }],
    });
  }

  protected open(summary: TrainingPlanSummary): void {
    this.formError.set(null);
    this.api.trainingPlan(summary.id).subscribe({
      next: (plan) => {
        this.draft.set(this.toDraft(plan));
        this.api.trainingPlanVersions(plan.id).subscribe({ next: (v) => this.versions.set(v), error: () => this.versions.set([]) });
      },
      error: (err: unknown) => this.notice.set(this.errors.message(err)),
    });
  }

  protected duplicate(summary: TrainingPlanSummary): void {
    this.api.duplicateTrainingPlan(summary.id).subscribe({
      next: () => {
        this.toast.success('trainingPlans.duplicatedToast');
        this.list.load();
      },
      error: (err: unknown) => this.notice.set(this.errors.message(err)),
    });
  }

  protected toggle(summary: TrainingPlanSummary): void {
    this.api.setTrainingPlanStatus(summary.id, summary.status !== 'Active').subscribe({
      next: () => {
        this.toast.success('trainingPlans.statusToast');
        this.list.load();
      },
      error: (err: unknown) => this.notice.set(this.errors.message(err)),
    });
  }

  protected patch(key: string, value: string): void {
    this.draft.update((d) => (d ? { ...d, [key]: value } : d));
  }

  protected addDay(): void {
    this.draft.update((d) => (d ? { ...d, days: [...d.days, { name: '', notes: '', exercises: [] }] } : d));
  }

  protected removeDay(i: number): void {
    this.draft.update((d) => (d ? { ...d, days: d.days.filter((_, k) => k !== i) } : d));
  }

  protected moveDay(i: number, delta: number): void {
    this.draft.update((d) => (d ? { ...d, days: move(d.days, i, delta) } : d));
  }

  protected patchDay(i: number, key: 'name' | 'notes', value: string): void {
    this.mapDay(i, (day) => ({ ...day, [key]: value }));
  }

  protected addExercise(i: number): void {
    this.mapDay(i, (day) => ({ ...day, exercises: [...day.exercises, { exerciseId: '', name: '', trackingMode: 'RepsAndLoad', sets: '3', reps: '10', seconds: '', rest: '60', load: '', notes: '' }] }));
  }

  protected removeExercise(i: number, j: number): void {
    this.mapDay(i, (day) => ({ ...day, exercises: day.exercises.filter((_, k) => k !== j) }));
  }

  protected moveExercise(i: number, j: number, delta: number): void {
    this.mapDay(i, (day) => ({ ...day, exercises: move(day.exercises, j, delta) }));
  }

  protected patchExercise(i: number, j: number, key: keyof ExerciseDraft, value: string): void {
    this.mapExercise(i, j, (ex) => ({ ...ex, [key]: value }));
  }

  protected chooseExercise(i: number, j: number, name: string): void {
    const found = this.library().find((e) => e.name.toLowerCase() === name.trim().toLowerCase());
    this.mapExercise(i, j, (ex) =>
      found ? { ...ex, exerciseId: found.id, name: found.name, trackingMode: found.trackingMode } : { ...ex, exerciseId: '', name },
    );
  }

  protected save(): void {
    const d = this.draft();
    if (!d) {
      return;
    }
    if (d.days.some((day) => day.exercises.some((e) => !e.exerciseId))) {
      this.formError.set(this.errors.text('trainingPlans.pickExercise'));
      return;
    }
    const body: TrainingPlanRequest = {
      name: d.name.trim(),
      goal: d.goal.trim(),
      description: d.description.trim() || null,
      level: d.level as 'Beginner',
      estimatedMinutes: toInt(d.estimatedMinutes) ?? 0,
      validFrom: d.validFrom || null,
      validUntil: d.validUntil || null,
      changeNote: d.id ? d.changeNote.trim() : '',
      days: d.days.map((day) => ({
        name: day.name.trim(),
        notes: day.notes.trim() || null,
        exercises: day.exercises.map((e) => ({
          exerciseId: e.exerciseId,
          sets: toInt(e.sets) ?? 0,
          reps: e.trackingMode === 'Duration' ? null : toInt(e.reps),
          durationSeconds: e.trackingMode === 'Duration' ? toInt(e.seconds) : null,
          restSeconds: toInt(e.rest) ?? 0,
          recommendedLoadKg: e.trackingMode === 'RepsAndLoad' ? toNumber(e.load) : null,
          notes: e.notes.trim() || null,
        })),
      })),
    };
    this.busy.set(true);
    this.formError.set(null);
    (d.id ? this.api.updateTrainingPlan(d.id, body) : this.api.createTrainingPlan(body)).subscribe({
      next: () => {
        this.busy.set(false);
        this.draft.set(null);
        this.toast.success('trainingPlans.savedToast');
        this.list.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  private toDraft(plan: TrainingPlanDetail): PlanDraft {
    return {
      id: plan.id,
      name: plan.name,
      goal: plan.goal ?? '',
      description: plan.description ?? '',
      level: plan.level,
      estimatedMinutes: String(plan.estimatedMinutes),
      validFrom: plan.validFrom ?? '',
      validUntil: plan.validUntil ?? '',
      changeNote: '',
      days: plan.days.map((day) => ({
        name: day.name,
        notes: day.notes ?? '',
        exercises: day.exercises.map((e) => ({
          exerciseId: e.exerciseId,
          name: e.exerciseName,
          trackingMode: e.trackingMode,
          sets: String(e.sets),
          reps: e.reps?.toString() ?? '',
          seconds: e.durationSeconds?.toString() ?? '',
          rest: String(e.restSeconds),
          load: e.recommendedLoadKg?.toString() ?? '',
          notes: e.notes ?? '',
        })),
      })),
    };
  }

  private mapDay(i: number, change: (day: DayDraft) => DayDraft): void {
    this.draft.update((d) => (d ? { ...d, days: d.days.map((day, k) => (k === i ? change(day) : day)) } : d));
  }

  private mapExercise(i: number, j: number, change: (ex: ExerciseDraft) => ExerciseDraft): void {
    this.mapDay(i, (day) => ({ ...day, exercises: day.exercises.map((ex, k) => (k === j ? change(ex) : ex)) }));
  }
}
