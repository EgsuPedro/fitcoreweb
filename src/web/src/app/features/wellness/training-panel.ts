import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { MemberTrainingPlan, TrainingAdherence, TrainingPlanSummary, WorkoutSession, ExerciseProgress } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { ChartPoint, clampPercent } from '../../shared/chart-math';
import { FcDate } from '../../shared/format.pipes';
import { LineChart } from '../../shared/line-chart';
import { Modal } from '../../shared/modal';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';
import { SessionDialog } from './session-dialog';

/**
 * Rutina del alumno: el plan vigente (con la guía de cada ejercicio), el registro de sesiones, la evolución por ejercicio y la
 * adherencia. La adherencia mide cumplimiento registrado frente a lo asignado, no resultados físicos.
 */
@Component({
  selector: 'fc-training-panel',
  imports: [TranslocoPipe, Badge, FcDate, LineChart, Modal, ReasonDialog, SessionDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    <section class="subcard">
      <h3>{{ 'training.currentPlan' | transloco }}</h3>
      @if (plan(); as p) {
        <p>
          <strong>{{ p.assignment.planName }}</strong>
          <small class="muted"> v{{ p.assignment.planVersion }} · {{ p.assignment.startDate | fcDate }} – {{ p.assignment.endDate ? (p.assignment.endDate | fcDate) : ('training.openEnded' | transloco) }}</small>
          <fc-badge tone="ok">{{ 'enums.assignmentStatus.' + p.assignment.status | transloco }}</fc-badge>
        </p>
        @if (p.goal) {
          <p class="muted">{{ p.goal }}</p>
        }
        @for (day of p.days; track day.id) {
          <div class="day-block">
            <h4>{{ day.name }}</h4>
            @if (day.notes) {
              <p class="muted">{{ day.notes }}</p>
            }
            <ul>
              @for (e of day.exercises; track e.prescription.id) {
                <li>
                  <details>
                    <summary>
                      <strong>{{ e.prescription.exerciseName }}</strong> —
                      @if (e.prescription.trackingMode === 'Duration') {
                        {{ e.prescription.sets }} × {{ e.prescription.durationSeconds }} s
                      } @else {
                        {{ e.prescription.sets }} × {{ e.prescription.reps }}
                        @if (e.prescription.recommendedLoadKg) { · {{ e.prescription.recommendedLoadKg }} kg }
                      }
                      · {{ 'training.rest' | transloco }} {{ e.prescription.restSeconds }} s
                    </summary>
                    @if (e.prescription.notes) {
                      <p>{{ e.prescription.notes }}</p>
                    }
                    @if (e.guide; as g) {
                      @if (g.instructions.length > 0) {
                        <ol>
                          @for (step of g.instructions; track $index) {
                            <li>{{ step }}</li>
                          }
                        </ol>
                      }
                      @if (g.commonMistakes.length > 0) {
                        <p class="muted"><strong>{{ 'training.mistakes' | transloco }}:</strong> {{ g.commonMistakes.join('; ') }}</p>
                      }
                      @if (g.warnings) {
                        <p class="alert alert-warning">{{ g.warnings }}</p>
                      }
                    }
                  </details>
                </li>
              }
            </ul>
          </div>
        }
        <div class="inline-actions">
          @if (self() && canLog()) {
            <button type="button" class="btn btn-primary" (click)="logging.set(true)">{{ 'sessions.log' | transloco }}</button>
          }
          @if (canAssign()) {
            <button type="button" class="btn" (click)="openAssign()">{{ 'training.change' | transloco }}</button>
            <button type="button" class="btn btn-danger" (click)="ending.set(p.assignment.id)">{{ 'training.end' | transloco }}</button>
          }
        </div>
      } @else {
        <p class="muted">{{ 'training.noPlan' | transloco }}</p>
        @if (canAssign()) {
          <div class="inline-actions"><button type="button" class="btn btn-primary" (click)="openAssign()">{{ 'training.assign' | transloco }}</button></div>
        }
      }
    </section>

    @if (adherence(); as a) {
      <section class="subcard">
        <h3>{{ 'adherence.training' | transloco }}</h3>
        <p>
          <strong>{{ a.percent !== null && a.percent !== undefined ? a.percent + ' %' : '—' }}</strong>
          <span class="muted"> · {{ 'adherence.trainingSummary' | transloco: { done: a.sessions, expected: a.expected } }}</span>
        </p>
        @for (w of a.weeks; track w.weekStart) {
          <div class="meter-row">
            <span>{{ w.weekStart | fcDate }}</span>
            <span class="meter" role="progressbar" aria-valuemin="0" aria-valuemax="100" [attr.aria-valuenow]="w.percent ?? 0" [attr.aria-label]="w.weekStart">
              <span [style.--w]="pct(w.percent)"></span>
            </span>
            <span class="num">{{ w.percent !== null && w.percent !== undefined ? w.percent + ' %' : '—' }}</span>
          </div>
        }
        <p class="muted"><small>{{ 'adherence.note' | transloco }}</small></p>
      </section>
    }

    @if (exerciseOptions().length > 0) {
      <section class="subcard">
        <h3>{{ 'training.progress' | transloco }}</h3>
        <label class="field inline-field">
          <span>{{ 'training.exercise' | transloco }}</span>
          <select [value]="exerciseId()" (change)="loadProgress($any($event.target).value)">
            <option value="">—</option>
            @for (o of exerciseOptions(); track o.id) {
              <option [value]="o.id">{{ o.name }}</option>
            }
          </select>
        </label>
        @if (progress(); as pr) {
          <fc-line-chart [points]="progressSeries()" [unit]="progressUnit()" [decimals]="0" [label]="pr.exerciseName" />
        } @else if (noProgress()) {
          <p class="muted">{{ 'training.noProgress' | transloco }}</p>
        }
      </section>
    }

    <section class="subcard">
      <h3>{{ 'sessions.history' | transloco }}</h3>
      @for (s of sessions(); track s.id) {
        <details class="subcard">
          <summary>
            <strong>{{ s.performedOn | fcDate }}</strong> · {{ s.dayName }}
            <span class="muted"> · {{ s.completedExercises }}/{{ s.totalExercises }} · {{ s.totalSets }} {{ 'sessions.sets' | transloco }}@if (s.volumeKg) { · {{ s.volumeKg }} kg }</span>
          </summary>
          @for (e of s.exercises; track e.planExerciseId) {
            <p>
              <strong>{{ e.exerciseName }}</strong>
              @if (e.completed) {
                <small class="muted">
                  @for (set of e.sets; track set.setNumber) {
                    <span> · {{ set.reps ?? set.durationSeconds + ' s' }}@if (set.loadKg) { × {{ set.loadKg }} kg }</span>
                  }
                </small>
              } @else {
                <small class="muted"> · {{ 'sessions.notDone' | transloco }}</small>
              }
            </p>
          }
          @if (s.memberNotes) {
            <p class="note-text">{{ s.memberNotes }}</p>
          }
          @if (s.trainerComment) {
            <p class="alert alert-info"><strong>{{ 'sessions.trainerComment' | transloco }}:</strong> {{ s.trainerComment }}</p>
          } @else if (canAssign() && !self()) {
            <button type="button" class="btn btn-ghost" (click)="commenting.set(s.id)">{{ 'sessions.comment' | transloco }}</button>
          }
        </details>
      } @empty {
        <p class="muted">{{ 'sessions.empty' | transloco }}</p>
      }
    </section>

    @if (logging() && plan(); as p) {
      <fc-session-dialog [memberId]="memberId()" [plan]="p" (closed)="logging.set(false)" (saved)="onLogged()" />
    }

    @if (assigning()) {
      <fc-modal [title]="'training.assign' | transloco" (closed)="assigning.set(false)">
        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <label class="field">
          <span>{{ 'training.plan' | transloco }}</span>
          <select [value]="planChoice()" (change)="planChoice.set($any($event.target).value)">
            <option value="">—</option>
            @for (c of choices(); track c.id) {
              <option [value]="c.id">{{ c.name }} (v{{ c.version }})</option>
            }
          </select>
        </label>
        <div class="form-grid">
          <label class="field"><span>{{ 'training.startDate' | transloco }}</span><input type="date" [value]="startDate()" (input)="startDate.set($any($event.target).value)" /><small class="muted">{{ 'training.startHint' | transloco }}</small></label>
          <label class="field"><span>{{ 'training.endDate' | transloco }}</span><input type="date" [value]="endDate()" (input)="endDate.set($any($event.target).value)" /></label>
        </div>
        <ng-container footer>
          <button type="button" class="btn" (click)="assigning.set(false)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn btn-primary" [disabled]="!planChoice() || busy()" (click)="assign()">{{ 'training.assign' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (ending(); as id) {
      <fc-reason-dialog
        [title]="'training.end' | transloco"
        [label]="'common.reason' | transloco"
        [confirmLabel]="'training.end' | transloco"
        [danger]="true"
        [minLength]="3"
        [busy]="busy()"
        [error]="formError()"
        (submitted)="end(id, $event)"
        (cancelled)="ending.set(null)"
      />
    }

    @if (commenting(); as id) {
      <fc-reason-dialog
        [title]="'sessions.comment' | transloco"
        [label]="'sessions.trainerComment' | transloco"
        [confirmLabel]="'common.save' | transloco"
        [minLength]="3"
        [busy]="busy()"
        [error]="formError()"
        (submitted)="comment(id, $event)"
        (cancelled)="commenting.set(null)"
      />
    }
  `,
})
export class TrainingPanel implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  readonly memberId = input.required<string>();
  readonly self = input(false);

  protected readonly plan = signal<MemberTrainingPlan | null>(null);
  protected readonly sessions = signal<WorkoutSession[]>([]);
  protected readonly adherence = signal<TrainingAdherence | null>(null);
  protected readonly progress = signal<ExerciseProgress | null>(null);
  protected readonly noProgress = signal(false);
  protected readonly exerciseId = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly logging = signal(false);
  protected readonly assigning = signal(false);
  protected readonly ending = signal<string | null>(null);
  protected readonly commenting = signal<string | null>(null);
  protected readonly choices = signal<TrainingPlanSummary[]>([]);
  protected readonly planChoice = signal('');
  protected readonly startDate = signal('');
  protected readonly endDate = signal('');
  protected readonly canAssign = computed(() => this.session.has(Perm.TrainingMembersManage));
  protected readonly canLog = computed(() => this.session.has(Perm.TrainingSessionsLog));

  protected readonly exerciseOptions = computed(() => {
    const seen = new Map<string, string>();
    for (const day of this.plan()?.days ?? []) {
      for (const e of day.exercises) {
        seen.set(e.prescription.exerciseId, e.prescription.exerciseName);
      }
    }
    return [...seen].map(([id, name]) => ({ id, name }));
  });

  protected readonly progressSeries = computed<ChartPoint[]>(() => {
    const pr = this.progress();
    if (!pr) {
      return [];
    }
    return pr.points.flatMap((p) => {
      const value = pr.trackingMode === 'RepsAndLoad' ? p.maxLoadKg : pr.trackingMode === 'Duration' ? p.totalDurationSeconds : p.totalReps;
      return typeof value === 'number' ? [{ label: p.date, value }] : [];
    });
  });

  protected readonly progressUnit = computed(() => {
    const mode = this.progress()?.trackingMode;
    return mode === 'RepsAndLoad' ? 'kg' : mode === 'Duration' ? 's' : this.errors.text('training.repsUnit');
  });

  ngOnInit(): void {
    this.reload();
  }

  protected pct(value: number | null | undefined): number {
    return clampPercent(value);
  }

  protected loadProgress(exerciseId: string): void {
    this.exerciseId.set(exerciseId);
    this.progress.set(null);
    this.noProgress.set(false);
    if (!exerciseId) {
      return;
    }
    this.api.exerciseProgress(this.memberId(), exerciseId).subscribe({
      next: (p) => this.progress.set(p),
      error: (err: unknown) => {
        if (this.errors.problemOf(err)?.code === 'training.progress_exercise_not_found') {
          this.noProgress.set(true);
        } else {
          this.error.set(this.errors.message(err));
        }
      },
    });
  }

  protected onLogged(): void {
    this.logging.set(false);
    this.toast.success('sessions.savedToast');
    this.reload();
  }

  protected openAssign(): void {
    this.formError.set(null);
    this.planChoice.set('');
    this.startDate.set('');
    this.endDate.set('');
    this.api.trainingPlans({ status: 'Active', pageSize: 100 }).subscribe({
      next: (page) => {
        this.choices.set(page.items);
        this.assigning.set(true);
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  protected assign(): void {
    this.busy.set(true);
    this.formError.set(null);
    this.api.assignTrainingPlan(this.planChoice(), { memberId: this.memberId(), startDate: this.startDate() || null, endDate: this.endDate() || null }).subscribe({
      next: () => {
        this.busy.set(false);
        this.assigning.set(false);
        this.toast.success('training.assignedToast');
        this.reload();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  protected end(id: string, reason: string): void {
    this.busy.set(true);
    this.formError.set(null);
    this.api.endTrainingAssignment(id, reason).subscribe({
      next: () => {
        this.busy.set(false);
        this.ending.set(null);
        this.toast.success('training.endedToast');
        this.reload();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  protected comment(sessionId: string, text: string): void {
    this.busy.set(true);
    this.formError.set(null);
    this.api.addTrainerComment(sessionId, text).subscribe({
      next: () => {
        this.busy.set(false);
        this.commenting.set(null);
        this.toast.success('sessions.commentedToast');
        this.reload();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  private reload(): void {
    const id = this.memberId();
    this.error.set(null);
    this.api.memberTrainingPlan(id).subscribe({
      next: (p) => this.plan.set(p),
      error: (err: unknown) => {
        this.plan.set(null);
        // «Sin rutina» no es un fallo: es el estado inicial de un alumno nuevo.
        if (this.errors.problemOf(err)?.code !== 'training.no_active_plan') {
          this.error.set(this.errors.message(err));
        }
      },
    });
    this.api.workoutSessions(id, { pageSize: 20 }).subscribe({
      next: (page) => this.sessions.set(page.items),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
    this.api.trainingAdherence(id).subscribe({
      next: (a) => this.adherence.set(a),
      error: () => this.adherence.set(null),
    });
  }
}
