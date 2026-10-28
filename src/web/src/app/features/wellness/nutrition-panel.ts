import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { DietaryProfile, MealLog, MemberNutritionPlan, NutritionAdherence, NutritionPlanSummary } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { clampPercent } from '../../shared/chart-math';
import { FcDate } from '../../shared/format.pipes';
import { toLines, todayIso } from '../../shared/form-utils';
import { Modal } from '../../shared/modal';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';

type MealStatus = 'Completed' | 'Partial' | 'Skipped';

/**
 * Nutrición del alumno: plan vigente, alergias y restricciones, registro de cumplimiento diario y adherencia. Datos sensibles.
 * Es orientación de seguimiento: no sustituye la valoración de un profesional ni calcula dietas automáticas.
 */
@Component({
  selector: 'fc-nutrition-panel',
  imports: [TranslocoPipe, Badge, FcDate, Modal, ReasonDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    <section class="subcard">
      <h3>{{ 'nutrition.profile' | transloco }}</h3>
      @if (profile(); as pf) {
        @if (pf.allergies.length > 0) {
          <p class="alert alert-danger"><strong>{{ 'nutrition.allergies' | transloco }}:</strong> {{ pf.allergies.join(', ') }}</p>
        }
        <dl class="kv">
          @if (pf.intolerances.length > 0) {
            <dt>{{ 'nutrition.intolerances' | transloco }}</dt>
            <dd>{{ pf.intolerances.join(', ') }}</dd>
          }
          @if (pf.restrictions.length > 0) {
            <dt>{{ 'nutrition.restrictions' | transloco }}</dt>
            <dd>{{ pf.restrictions.join(', ') }}</dd>
          }
          @if (pf.notes) {
            <dt>{{ 'common.notes' | transloco }}</dt>
            <dd class="note-text">{{ pf.notes }}</dd>
          }
        </dl>
      } @else {
        <p class="muted">{{ 'nutrition.noProfile' | transloco }}</p>
      }
      @if (canProfile()) {
        <div class="inline-actions"><button type="button" class="btn" (click)="openProfile()">{{ 'nutrition.editProfile' | transloco }}</button></div>
      }
    </section>

    <section class="subcard">
      <h3>{{ 'nutrition.currentPlan' | transloco }}</h3>
      @if (plan(); as p) {
        <p>
          <strong>{{ p.plan.name }}</strong>
          <small class="muted"> v{{ p.assignment.planVersion }} · {{ p.assignment.startDate | fcDate }} – {{ p.assignment.endDate ? (p.assignment.endDate | fcDate) : ('training.openEnded' | transloco) }}</small>
          <fc-badge tone="ok">{{ 'enums.assignmentStatus.' + p.assignment.status | transloco }}</fc-badge>
        </p>
        @if (p.plan.goal) {
          <p class="muted">{{ p.plan.goal }}</p>
        }
        @if (p.plan.targetCalories) {
          <p>
            <strong>{{ p.plan.targetCalories }} kcal</strong>
            @if (p.plan.targetProteinG) { · {{ 'nutrition.protein' | transloco }} {{ p.plan.targetProteinG }} g }
            @if (p.plan.targetCarbsG) { · {{ 'nutrition.carbs' | transloco }} {{ p.plan.targetCarbsG }} g }
            @if (p.plan.targetFatG) { · {{ 'nutrition.fat' | transloco }} {{ p.plan.targetFatG }} g }
          </p>
        }
        @for (meal of p.plan.meals; track meal.id) {
          <div class="day-block">
            <h4>
              {{ meal.name }}
              @if (meal.suggestedTime) { <small class="muted">{{ meal.suggestedTime.slice(0, 5) }}</small> }
              @if (meal.estimatedCalories) { <small class="muted"> · {{ meal.estimatedCalories }} kcal</small> }
            </h4>
            @if (meal.notes) {
              <p class="muted">{{ meal.notes }}</p>
            }
            <ul>
              @for (item of meal.items; track item.id) {
                <li>{{ item.foodName }} — {{ item.quantity }} {{ item.unit }}@if (item.calories) { <small class="muted"> · {{ item.calories }} kcal</small> }</li>
              }
            </ul>
            @if (canRecord()) {
              <div class="inline-actions" role="group" [attr.aria-label]="meal.name">
                @if (todayLog(meal.id); as log) {
                  <fc-badge [tone]="log.status === 'Completed' ? 'ok' : log.status === 'Partial' ? 'warn' : 'neutral'">{{ 'enums.mealLogStatus.' + log.status | transloco }}</fc-badge>
                } @else {
                  <span class="muted">{{ 'nutrition.today' | transloco }}:</span>
                  @for (s of statuses; track s) {
                    <button type="button" class="btn btn-ghost" [disabled]="busy()" (click)="log(meal.id, s)">{{ 'enums.mealLogStatus.' + s | transloco }}</button>
                  }
                }
              </div>
            }
          </div>
        }
        @if (p.plan.recommendations) {
          <p class="note-text">{{ p.plan.recommendations }}</p>
        }
        <p class="muted"><small>{{ 'nutrition.disclaimer' | transloco }}</small></p>
        @if (canAssign()) {
          <div class="inline-actions">
            <button type="button" class="btn" (click)="openAssign()">{{ 'training.change' | transloco }}</button>
            <button type="button" class="btn btn-danger" (click)="ending.set(p.assignment.id)">{{ 'training.end' | transloco }}</button>
          </div>
        }
      } @else {
        <p class="muted">{{ 'nutrition.noPlan' | transloco }}</p>
        @if (canAssign()) {
          <div class="inline-actions"><button type="button" class="btn btn-primary" (click)="openAssign()">{{ 'nutrition.assign' | transloco }}</button></div>
        }
      }
    </section>

    @if (adherence(); as a) {
      <section class="subcard">
        <h3>{{ 'adherence.nutrition' | transloco }}</h3>
        <p>
          <strong>{{ a.percent !== null && a.percent !== undefined ? a.percent + ' %' : '—' }}</strong>
          <span class="muted"> · {{ 'adherence.nutritionSummary' | transloco: { completed: a.completed, partial: a.partial, skipped: a.skipped, notLogged: a.notLogged } }}</span>
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

    @if (editingProfile()) {
      <fc-modal [title]="'nutrition.editProfile' | transloco" (closed)="editingProfile.set(false)">
        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <label class="field"><span>{{ 'nutrition.allergies' | transloco }}</span><textarea [value]="draft().allergies" (input)="patch('allergies', $any($event.target).value)"></textarea><small class="muted">{{ 'exercises.linesHint' | transloco }}</small></label>
        <label class="field"><span>{{ 'nutrition.intolerances' | transloco }}</span><textarea [value]="draft().intolerances" (input)="patch('intolerances', $any($event.target).value)"></textarea></label>
        <label class="field"><span>{{ 'nutrition.restrictions' | transloco }}</span><textarea [value]="draft().restrictions" (input)="patch('restrictions', $any($event.target).value)"></textarea></label>
        <label class="field"><span>{{ 'common.notes' | transloco }}</span><textarea maxlength="1000" [value]="draft().notes" (input)="patch('notes', $any($event.target).value)"></textarea><small class="muted">{{ 'nutrition.profileHint' | transloco }}</small></label>
        <ng-container footer>
          <button type="button" class="btn" (click)="editingProfile.set(false)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="saveProfile()">{{ 'common.save' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (assigning()) {
      <fc-modal [title]="'nutrition.assign' | transloco" (closed)="assigning.set(false)">
        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <label class="field">
          <span>{{ 'nutrition.plan' | transloco }}</span>
          <select [value]="planChoice()" (change)="planChoice.set($any($event.target).value)">
            <option value="">—</option>
            @for (c of choices(); track c.id) {
              <option [value]="c.id">{{ c.name }} (v{{ c.version }})</option>
            }
          </select>
        </label>
        <div class="form-grid">
          <label class="field"><span>{{ 'training.startDate' | transloco }}</span><input type="date" [value]="startDate()" (input)="startDate.set($any($event.target).value)" /></label>
          <label class="field"><span>{{ 'training.endDate' | transloco }}</span><input type="date" [value]="endDate()" (input)="endDate.set($any($event.target).value)" /></label>
        </div>
        <ng-container footer>
          <button type="button" class="btn" (click)="assigning.set(false)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn btn-primary" [disabled]="!planChoice() || busy()" (click)="assign()">{{ 'nutrition.assign' | transloco }}</button>
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
  `,
})
export class NutritionPanel implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  readonly memberId = input.required<string>();
  readonly self = input(false);

  protected readonly statuses: readonly MealStatus[] = ['Completed', 'Partial', 'Skipped'];
  protected readonly plan = signal<MemberNutritionPlan | null>(null);
  protected readonly profile = signal<DietaryProfile | null>(null);
  protected readonly adherence = signal<NutritionAdherence | null>(null);
  protected readonly logs = signal<MealLog[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly editingProfile = signal(false);
  protected readonly assigning = signal(false);
  protected readonly ending = signal<string | null>(null);
  protected readonly choices = signal<NutritionPlanSummary[]>([]);
  protected readonly planChoice = signal('');
  protected readonly startDate = signal('');
  protected readonly endDate = signal('');
  protected readonly draft = signal({ allergies: '', intolerances: '', restrictions: '', notes: '' });
  protected readonly canProfile = computed(() => this.session.has(Perm.NutritionProfileManage));
  protected readonly canAssign = computed(() => this.session.has(Perm.NutritionMembersManage));
  protected readonly canRecord = computed(() => this.self() && this.session.has(Perm.NutritionLogsRecord));

  ngOnInit(): void {
    this.reload();
  }

  protected pct(value: number | null | undefined): number {
    return clampPercent(value);
  }

  protected todayLog(mealId: string): MealLog | undefined {
    return this.logs().find((l) => l.mealId === mealId);
  }

  protected patch(key: string, value: string): void {
    this.draft.update((d) => ({ ...d, [key]: value }));
  }

  protected log(mealId: string, status: MealStatus): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.logMeal({ memberId: this.memberId(), mealId, date: todayIso(), status, notes: null }).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success('nutrition.loggedToast');
        this.reload();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected openProfile(): void {
    const pf = this.profile();
    this.draft.set({
      allergies: pf?.allergies.join('\n') ?? '',
      intolerances: pf?.intolerances.join('\n') ?? '',
      restrictions: pf?.restrictions.join('\n') ?? '',
      notes: pf?.notes ?? '',
    });
    this.formError.set(null);
    this.editingProfile.set(true);
  }

  protected saveProfile(): void {
    const d = this.draft();
    this.busy.set(true);
    this.formError.set(null);
    this.api
      .saveDietaryProfile(this.memberId(), {
        allergies: toLines(d.allergies),
        intolerances: toLines(d.intolerances),
        restrictions: toLines(d.restrictions),
        notes: d.notes.trim() || null,
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.editingProfile.set(false);
          this.toast.success('nutrition.profileSavedToast');
          this.reload();
        },
        error: (err: unknown) => {
          this.busy.set(false);
          this.formError.set(this.errors.message(err));
        },
      });
  }

  protected openAssign(): void {
    this.formError.set(null);
    this.planChoice.set('');
    this.startDate.set('');
    this.endDate.set('');
    this.api.nutritionPlans({ status: 'Active', pageSize: 100 }).subscribe({
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
    this.api.assignNutritionPlan(this.planChoice(), { memberId: this.memberId(), startDate: this.startDate() || null, endDate: this.endDate() || null }).subscribe({
      next: () => {
        this.busy.set(false);
        this.assigning.set(false);
        this.toast.success('nutrition.assignedToast');
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
    this.api.endNutritionAssignment(id, reason).subscribe({
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

  private reload(): void {
    const id = this.memberId();
    const today = todayIso();
    this.error.set(null);
    this.api.memberNutritionPlan(id).subscribe({
      next: (p) => this.plan.set(p),
      error: (err: unknown) => {
        this.plan.set(null);
        if (this.errors.problemOf(err)?.code !== 'nutrition.no_active_plan') {
          this.error.set(this.errors.message(err));
        }
      },
    });
    this.api.dietaryProfile(id).subscribe({
      next: (list) => this.profile.set(list[0] ?? null),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
    this.api.mealLogs(id, { from: today, to: today }).subscribe({
      next: (r) => this.logs.set(r.logs),
      error: () => this.logs.set([]),
    });
    this.api.nutritionAdherence(id).subscribe({
      next: (a) => this.adherence.set(a),
      error: () => this.adherence.set(null),
    });
  }
}
