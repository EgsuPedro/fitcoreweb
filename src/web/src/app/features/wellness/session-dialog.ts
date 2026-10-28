import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { MemberTrainingPlan, WorkoutSession } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { toInt, toNumber, todayIso } from '../../shared/form-utils';
import { Modal } from '../../shared/modal';

interface SetDraft {
  reps: string;
  seconds: string;
  load: string;
}

interface ExerciseDraft {
  planExerciseId: string;
  name: string;
  trackingMode: string;
  prescription: string;
  completed: boolean;
  sets: SetDraft[];
}

/**
 * Registro de una sesión de entrenamiento del propio alumno: elige el día de su rutina, marca los ejercicios que hizo y anota
 * sus series. Lo que se pide por serie depende de cómo se registra cada ejercicio (repeticiones con carga, repeticiones o tiempo).
 */
@Component({
  selector: 'fc-session-dialog',
  imports: [Modal, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="'sessions.log' | transloco" (closed)="closed.emit()">
      @if (error()) {
        <p class="alert alert-danger" role="alert">{{ error() }}</p>
      }
      <div class="form-grid">
        <label class="field">
          <span>{{ 'sessions.day' | transloco }}</span>
          <select [value]="dayId()" (change)="selectDay($any($event.target).value)">
            @for (d of plan().days; track d.id) {
              <option [value]="d.id">{{ d.name }}</option>
            }
          </select>
        </label>
        <label class="field"><span>{{ 'sessions.date' | transloco }}</span><input type="date" [value]="date()" (input)="date.set($any($event.target).value)" /></label>
        <label class="field"><span>{{ 'sessions.duration' | transloco }}</span><input inputmode="numeric" [value]="minutes()" (input)="minutes.set($any($event.target).value)" /></label>
      </div>

      @for (ex of exercises(); track ex.planExerciseId; let i = $index) {
        <div class="subcard">
          <label class="check">
            <input type="checkbox" [checked]="ex.completed" (change)="toggle(i, $any($event.target).checked)" />
            <span><strong>{{ ex.name }}</strong> <small class="muted">{{ ex.prescription }}</small></span>
          </label>
          @if (ex.completed) {
            @for (s of ex.sets; track $index; let j = $index) {
              <div class="row-form">
                <span class="muted">{{ 'sessions.set' | transloco: { n: j + 1 } }}</span>
                @if (ex.trackingMode !== 'Duration') {
                  <label class="field"><span>{{ 'sessions.reps' | transloco }}</span><input inputmode="numeric" [value]="s.reps" (input)="setField(i, j, 'reps', $any($event.target).value)" /></label>
                }
                @if (ex.trackingMode === 'Duration') {
                  <label class="field"><span>{{ 'sessions.seconds' | transloco }}</span><input inputmode="numeric" [value]="s.seconds" (input)="setField(i, j, 'seconds', $any($event.target).value)" /></label>
                }
                @if (ex.trackingMode === 'RepsAndLoad') {
                  <label class="field"><span>{{ 'sessions.load' | transloco }}</span><input inputmode="decimal" [value]="s.load" (input)="setField(i, j, 'load', $any($event.target).value)" /></label>
                }
                <button type="button" class="btn btn-ghost" [attr.aria-label]="'sessions.removeSet' | transloco" (click)="removeSet(i, j)">×</button>
              </div>
            }
            <button type="button" class="btn btn-ghost" (click)="addSet(i)">{{ 'sessions.addSet' | transloco }}</button>
          }
        </div>
      }

      <label class="field"><span>{{ 'common.notes' | transloco }}</span><textarea maxlength="500" [value]="notes()" (input)="notes.set($any($event.target).value)"></textarea></label>

      <ng-container footer>
        <button type="button" class="btn" (click)="closed.emit()">{{ 'common.cancel' | transloco }}</button>
        <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="save()">{{ 'sessions.save' | transloco }}</button>
      </ng-container>
    </fc-modal>
  `,
})
export class SessionDialog {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);

  readonly memberId = input.required<string>();
  readonly plan = input.required<MemberTrainingPlan>();
  readonly saved = output<WorkoutSession>();
  readonly closed = output<void>();

  protected readonly dayId = signal('');
  protected readonly date = signal(todayIso());
  protected readonly minutes = signal('');
  protected readonly notes = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly drafts = signal<ExerciseDraft[]>([]);
  protected readonly exercises = computed(() => this.drafts());

  constructor() {
    queueMicrotask(() => this.selectDay(this.plan().days[0]?.id ?? ''));
  }

  protected selectDay(id: string): void {
    this.dayId.set(id);
    const day = this.plan().days.find((d) => d.id === id);
    this.drafts.set(
      (day?.exercises ?? []).map((e) => {
        const p = e.prescription;
        const target = p.trackingMode === 'Duration' ? `${p.sets} × ${p.durationSeconds ?? '?'} s` : `${p.sets} × ${p.reps ?? '?'}${p.recommendedLoadKg ? ` · ${p.recommendedLoadKg} kg` : ''}`;
        return { planExerciseId: p.id, name: p.exerciseName, trackingMode: p.trackingMode, prescription: target, completed: false, sets: [] };
      }),
    );
  }

  protected toggle(i: number, completed: boolean): void {
    this.drafts.update((list) =>
      list.map((e, index) => (index === i ? { ...e, completed, sets: completed && e.sets.length === 0 ? [{ reps: '', seconds: '', load: '' }] : e.sets } : e)),
    );
  }

  protected addSet(i: number): void {
    this.drafts.update((list) => list.map((e, index) => (index === i ? { ...e, sets: [...e.sets, { reps: '', seconds: '', load: '' }] } : e)));
  }

  protected removeSet(i: number, j: number): void {
    this.drafts.update((list) => list.map((e, index) => (index === i ? { ...e, sets: e.sets.filter((_, k) => k !== j) } : e)));
  }

  protected setField(i: number, j: number, field: keyof SetDraft, value: string): void {
    this.drafts.update((list) =>
      list.map((e, index) => (index === i ? { ...e, sets: e.sets.map((s, k) => (k === j ? { ...s, [field]: value } : s)) } : e)),
    );
  }

  protected save(): void {
    this.busy.set(true);
    this.error.set(null);
    this.api
      .createWorkoutSession({
        memberId: this.memberId(),
        dayId: this.dayId(),
        performedOn: this.date(),
        durationMinutes: toInt(this.minutes()),
        notes: this.notes().trim() || null,
        exercises: this.drafts().map((e) => ({
          planExerciseId: e.planExerciseId,
          completed: e.completed,
          notes: null,
          sets: e.completed
            ? e.sets.map((s) => ({
                reps: e.trackingMode === 'Duration' ? null : toInt(s.reps),
                durationSeconds: e.trackingMode === 'Duration' ? toInt(s.seconds) : null,
                loadKg: e.trackingMode === 'RepsAndLoad' ? toNumber(s.load) : null,
              }))
            : [],
        })),
      })
      .subscribe({
        next: (session) => {
          this.busy.set(false);
          this.saved.emit(session);
        },
        error: (err: unknown) => {
          this.busy.set(false);
          this.error.set(this.errors.message(err));
        },
      });
  }
}
