import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { Observable } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { Exercise, ExerciseRequest } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { Confirm } from '../../shared/confirm';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

type Muscle = ExerciseRequest['primaryMuscle'];
type ExType = ExerciseRequest['type'];
type Equip = ExerciseRequest['equipment'];
type Level = ExerciseRequest['difficulty'];
type Tracking = ExerciseRequest['trackingMode'];
type Dialog = 'view' | 'form' | 'status' | null;

const MUSCLES: Muscle[] = ['Chest', 'Back', 'Shoulders', 'Biceps', 'Triceps', 'Forearms', 'Core', 'Quadriceps', 'Hamstrings', 'Glutes', 'Calves', 'FullBody'];
const TYPES: ExType[] = ['Strength', 'Cardio', 'Mobility', 'Flexibility', 'Balance', 'Plyometric'];
const EQUIPMENT: Equip[] = ['BodyWeight', 'Barbell', 'Dumbbell', 'Kettlebell', 'Machine', 'Cable', 'Band', 'Bench', 'PullUpBar', 'CardioMachine', 'Other'];
const LEVELS: Level[] = ['Beginner', 'Intermediate', 'Advanced'];
const TRACKING: Tracking[] = ['RepsAndLoad', 'Reps', 'Duration'];

const lines = (text: string): string[] => text.split('\n').map((line) => line.trim()).filter((line) => line.length > 0);

/**
 * Biblioteca de ejercicios. Para un gimnasio muestra el catálogo base (solo lectura, se puede duplicar) y sus propios ejercicios
 * (editables). En la administración de plataforma (`data.platform`) gestiona el catálogo base.
 */
@Component({
  selector: 'fc-exercises',
  imports: [ReactiveFormsModule, TranslocoPipe, Badge, Confirm, Modal, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ (platform ? 'exercises.globalTitle' : 'exercises.title') | transloco }}</h1>
      @if (canManage()) {
        <button type="button" class="btn btn-primary" (click)="openForm(null)">{{ 'exercises.new' | transloco }}</button>
      }
    </header>
    @if (!platform) {
      <p class="muted">{{ 'exercises.baseNote' | transloco }}</p>
    }

    <div class="toolbar">
      <input type="search" class="search" [placeholder]="'exercises.searchHint' | transloco" [attr.aria-label]="'exercises.searchHint' | transloco" (input)="list.search($any($event.target).value)" />
      @if (!platform) {
        <select [attr.aria-label]="'exercises.origin' | transloco" (change)="list.set({ scope: $any($event.target).value })">
          <option value="">{{ 'common.all' | transloco }}</option>
          <option value="global">{{ 'exercises.scopeGlobal' | transloco }}</option>
          <option value="own">{{ 'exercises.scopeOwn' | transloco }}</option>
        </select>
      }
      <select [attr.aria-label]="'exercises.muscle' | transloco" (change)="list.set({ muscle: $any($event.target).value })">
        <option value="">{{ 'exercises.allMuscles' | transloco }}</option>
        @for (m of muscles; track m) {
          <option [value]="m">{{ 'enums.muscleGroup.' + m | transloco }}</option>
        }
      </select>
      <select [attr.aria-label]="'exercises.equipment' | transloco" (change)="list.set({ equipment: $any($event.target).value })">
        <option value="">{{ 'exercises.allEquipment' | transloco }}</option>
        @for (e of equipment; track e) {
          <option [value]="e">{{ 'enums.equipment.' + e | transloco }}</option>
        }
      </select>
      <select [attr.aria-label]="'exercises.difficulty' | transloco" (change)="list.set({ difficulty: $any($event.target).value })">
        <option value="">{{ 'exercises.allLevels' | transloco }}</option>
        @for (l of levels; track l) {
          <option [value]="l">{{ 'enums.difficulty.' + l | transloco }}</option>
        }
      </select>
      <select [attr.aria-label]="'exercises.type' | transloco" (change)="list.set({ type: $any($event.target).value })">
        <option value="">{{ 'exercises.allTypes' | transloco }}</option>
        @for (t of types; track t) {
          <option [value]="t">{{ 'enums.exerciseType.' + t | transloco }}</option>
        }
      </select>
      <select [attr.aria-label]="'common.status' | transloco" (change)="list.set({ isActive: $any($event.target).value })">
        <option value="">{{ 'common.all' | transloco }}</option>
        <option value="true">{{ 'common.active' | transloco }}</option>
        <option value="false">{{ 'common.inactive' | transloco }}</option>
      </select>
    </div>

    @if (list.error()) {
      <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'exercises.name' | transloco }}</th>
            <th>{{ 'exercises.muscle' | transloco }}</th>
            <th>{{ 'exercises.equipment' | transloco }}</th>
            <th>{{ 'exercises.difficulty' | transloco }}</th>
            <th>{{ 'exercises.type' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (e of list.items(); track e.id) {
            <tr>
              <td>
                <a href="#" (click)="$event.preventDefault(); open(e)">{{ e.name }}</a>
                @if (e.isGlobal && !platform) {
                  <fc-badge tone="info">{{ 'exercises.base' | transloco }}</fc-badge>
                }
              </td>
              <td>{{ 'enums.muscleGroup.' + e.primaryMuscle | transloco }}</td>
              <td>{{ 'enums.equipment.' + e.equipment | transloco }}</td>
              <td>{{ 'enums.difficulty.' + e.difficulty | transloco }}</td>
              <td>{{ 'enums.exerciseType.' + e.type | transloco }}</td>
              <td><fc-badge [tone]="e.isActive ? 'ok' : 'off'">{{ (e.isActive ? 'common.active' : 'common.inactive') | transloco }}</fc-badge></td>
              <td class="actions">
                <button type="button" class="btn" (click)="open(e)">{{ 'common.view' | transloco }}</button>
                @if (canManage() && (platform || !e.isGlobal)) {
                  <button type="button" class="btn" (click)="openForm(e)">{{ 'common.edit' | transloco }}</button>
                  <button type="button" class="btn" (click)="startStatus(e)">{{ (e.isActive ? 'exercises.deactivate' : 'exercises.activate') | transloco }}</button>
                }
                @if (canManage() && !platform) {
                  <button type="button" class="btn" (click)="duplicate(e)">{{ 'exercises.duplicate' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="7" class="empty">{{ (list.loading() ? 'common.loading' : 'exercises.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @switch (dialog()) {
      @case ('view') {
        @if (selected(); as e) {
          <fc-modal [title]="e.name" (closed)="dialog.set(null)">
            @if (e.isGlobal && !platform) {
              <p class="alert alert-info">{{ 'exercises.baseWarning' | transloco }}</p>
            }
            @if (e.description) {
              <p>{{ e.description }}</p>
            }
            <dl class="kv">
              <dt>{{ 'exercises.muscle' | transloco }}</dt>
              <dd>
                {{ 'enums.muscleGroup.' + e.primaryMuscle | transloco }}
                @for (m of e.secondaryMuscles; track m) {
                  <span class="badge">{{ 'enums.muscleGroup.' + m | transloco }}</span>
                }
              </dd>
              <dt>{{ 'exercises.equipment' | transloco }}</dt>
              <dd>{{ 'enums.equipment.' + e.equipment | transloco }}</dd>
              <dt>{{ 'exercises.difficulty' | transloco }}</dt>
              <dd>{{ 'enums.difficulty.' + e.difficulty | transloco }}</dd>
              <dt>{{ 'exercises.tracking' | transloco }}</dt>
              <dd>{{ 'enums.trackingMode.' + e.trackingMode | transloco }}</dd>
            </dl>
            @if (e.instructions.length > 0) {
              <h3>{{ 'exercises.instructions' | transloco }}</h3>
              <ol>
                @for (step of e.instructions; track $index) {
                  <li>{{ step }}</li>
                }
              </ol>
            }
            @if (e.commonMistakes.length > 0) {
              <h3>{{ 'exercises.mistakes' | transloco }}</h3>
              <ul>
                @for (m of e.commonMistakes; track $index) {
                  <li>{{ m }}</li>
                }
              </ul>
            }
            @if (e.warnings) {
              <p class="alert alert-warning"><strong>{{ 'exercises.warnings' | transloco }}:</strong> {{ e.warnings }}</p>
            }
            @if (e.videoUrl) {
              <p><a [href]="e.videoUrl" target="_blank" rel="noopener noreferrer">{{ 'exercises.video' | transloco }}</a></p>
            }
            <ng-container footer>
              <button type="button" class="btn btn-primary" (click)="dialog.set(null)">{{ 'common.close' | transloco }}</button>
            </ng-container>
          </fc-modal>
        }
      }
      @case ('form') {
        <fc-modal [title]="(selected() ? 'exercises.edit' : 'exercises.new') | transloco" (closed)="dialog.set(null)">
          <form id="exercise-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
            @if (formError()) {
              <p class="alert alert-danger" role="alert">{{ formError() }}</p>
            }
            <label class="field">
              <span>{{ 'exercises.name' | transloco }}</span>
              <input formControlName="name" maxlength="120" />
            </label>
            <label class="field">
              <span>{{ 'exercises.description' | transloco }}</span>
              <textarea formControlName="description" maxlength="1000"></textarea>
            </label>
            <div class="form-grid">
              <label class="field">
                <span>{{ 'exercises.muscle' | transloco }}</span>
                <select formControlName="primaryMuscle">
                  @for (m of muscles; track m) {
                    <option [value]="m">{{ 'enums.muscleGroup.' + m | transloco }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span>{{ 'exercises.type' | transloco }}</span>
                <select formControlName="type">
                  @for (t of types; track t) {
                    <option [value]="t">{{ 'enums.exerciseType.' + t | transloco }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span>{{ 'exercises.equipment' | transloco }}</span>
                <select formControlName="equipment">
                  @for (e of equipment; track e) {
                    <option [value]="e">{{ 'enums.equipment.' + e | transloco }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span>{{ 'exercises.difficulty' | transloco }}</span>
                <select formControlName="difficulty">
                  @for (l of levels; track l) {
                    <option [value]="l">{{ 'enums.difficulty.' + l | transloco }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span>{{ 'exercises.tracking' | transloco }}</span>
                <select formControlName="trackingMode">
                  @for (t of tracking; track t) {
                    <option [value]="t">{{ 'enums.trackingMode.' + t | transloco }}</option>
                  }
                </select>
                <small class="muted">{{ 'exercises.trackingHint' | transloco }}</small>
              </label>
            </div>
            <fieldset class="field">
              <legend>{{ 'exercises.secondary' | transloco }}</legend>
              <small class="muted">{{ 'exercises.secondaryHint' | transloco }}</small>
              @for (m of muscles; track m) {
                @if (m !== form.controls.primaryMuscle.value) {
                  <label class="check">
                    <input type="checkbox" [checked]="secondary().has(m)" (change)="toggleSecondary(m, $any($event.target).checked)" />
                    <span>{{ 'enums.muscleGroup.' + m | transloco }}</span>
                  </label>
                }
              }
            </fieldset>
            <label class="field">
              <span>{{ 'exercises.instructions' | transloco }}</span>
              <textarea formControlName="instructions" rows="5"></textarea>
              <small class="muted">{{ 'exercises.linesHint' | transloco }}</small>
            </label>
            <label class="field">
              <span>{{ 'exercises.mistakes' | transloco }}</span>
              <textarea formControlName="commonMistakes" rows="3"></textarea>
              <small class="muted">{{ 'exercises.linesHint' | transloco }}</small>
            </label>
            <label class="field">
              <span>{{ 'exercises.warnings' | transloco }}</span>
              <textarea formControlName="warnings" maxlength="1000"></textarea>
            </label>
            <label class="field">
              <span>{{ 'exercises.video' | transloco }}</span>
              <input formControlName="videoUrl" type="url" maxlength="500" placeholder="https://" />
              <small class="muted">{{ 'exercises.videoHint' | transloco }}</small>
            </label>
          </form>
          <ng-container footer>
            <button type="button" class="btn" (click)="dialog.set(null)">{{ 'common.cancel' | transloco }}</button>
            <button type="submit" form="exercise-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
          </ng-container>
        </fc-modal>
      }
      @case ('status') {
        @if (selected(); as e) {
          <fc-confirm
            [title]="(e.isActive ? 'exercises.deactivate' : 'exercises.activate') | transloco"
            [message]="(e.isActive ? 'exercises.deactivateConfirm' : 'exercises.activateConfirm') | transloco: { name: e.name }"
            [confirmLabel]="(e.isActive ? 'exercises.deactivate' : 'exercises.activate') | transloco"
            [danger]="e.isActive"
            (confirmed)="toggle(e)"
            (cancelled)="dialog.set(null)"
          />
        }
      }
    }
  `,
})
export class Exercises implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  protected readonly platform = inject(ActivatedRoute).snapshot.data['platform'] === true;
  protected readonly list = new ListState<Exercise>(
    (query) => (this.platform ? this.api.globalExercises(query) : this.api.exercises(query)),
    this.errors,
    inject(DestroyRef),
  );

  protected readonly muscles = MUSCLES;
  protected readonly types = TYPES;
  protected readonly equipment = EQUIPMENT;
  protected readonly levels = LEVELS;
  protected readonly tracking = TRACKING;

  protected readonly canManage = computed(() => this.session.has(this.platform ? Perm.PlatformExercisesManage : Perm.ExercisesManage));
  protected readonly dialog = signal<Dialog>(null);
  protected readonly selected = signal<Exercise | null>(null);
  protected readonly secondary = signal<Set<string>>(new Set());
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(120)]],
    description: [''],
    primaryMuscle: ['Chest' as Muscle],
    type: ['Strength' as ExType],
    equipment: ['BodyWeight' as Equip],
    difficulty: ['Beginner' as Level],
    trackingMode: ['RepsAndLoad' as Tracking],
    instructions: [''],
    commonMistakes: [''],
    warnings: [''],
    videoUrl: ['', [Validators.pattern(/^$|^https:\/\/\S+$/i)]],
  });

  ngOnInit(): void {
    this.list.load();
  }

  protected open(exercise: Exercise): void {
    this.selected.set(exercise);
    this.dialog.set('view');
  }

  protected openForm(exercise: Exercise | null): void {
    this.selected.set(exercise);
    this.formError.set(null);
    if (exercise) {
      this.form.reset({
        name: exercise.name,
        description: exercise.description ?? '',
        primaryMuscle: exercise.primaryMuscle as Muscle,
        type: exercise.type as ExType,
        equipment: exercise.equipment as Equip,
        difficulty: exercise.difficulty as Level,
        trackingMode: exercise.trackingMode as Tracking,
        instructions: exercise.instructions.join('\n'),
        commonMistakes: exercise.commonMistakes.join('\n'),
        warnings: exercise.warnings ?? '',
        videoUrl: exercise.videoUrl ?? '',
      });
      this.secondary.set(new Set(exercise.secondaryMuscles));
    } else {
      this.form.reset();
      this.secondary.set(new Set());
    }
    this.dialog.set('form');
  }

  protected toggleSecondary(muscle: string, checked: boolean): void {
    const next = new Set(this.secondary());
    if (checked) {
      next.add(muscle);
    } else {
      next.delete(muscle);
    }
    this.secondary.set(next);
  }

  protected save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.errors.text('exercises.invalidForm'));
      return;
    }
    const v = this.form.getRawValue();
    const body: ExerciseRequest = {
      name: v.name.trim(),
      description: v.description.trim() || null,
      primaryMuscle: v.primaryMuscle,
      secondaryMuscles: [...this.secondary()].filter((m) => m !== v.primaryMuscle) as Muscle[],
      type: v.type,
      equipment: v.equipment,
      difficulty: v.difficulty,
      trackingMode: v.trackingMode,
      instructions: lines(v.instructions),
      commonMistakes: lines(v.commonMistakes),
      warnings: v.warnings.trim() || null,
      videoUrl: v.videoUrl.trim() || null,
    };
    this.run(this.api.saveExercise(body, this.selected()?.id ?? null, this.platform), 'exercises.savedToast', true);
  }

  protected startStatus(exercise: Exercise): void {
    this.selected.set(exercise);
    this.dialog.set('status');
  }

  protected toggle(exercise: Exercise): void {
    this.run(this.api.setExerciseStatus(exercise.id, !exercise.isActive, this.platform), 'exercises.savedToast', false);
  }

  protected duplicate(exercise: Exercise): void {
    this.run(this.api.duplicateExercise(exercise.id), 'exercises.duplicatedToast', false);
  }

  /** El aviso de éxito aparece solo cuando el servidor confirmó; un error se muestra en el formulario o en la lista. */
  private run(request: Observable<unknown>, toastKey: string, inForm: boolean): void {
    if (this.saving()) {
      return;
    }
    this.saving.set(true);
    this.formError.set(null);
    request.subscribe({
      next: () => {
        this.saving.set(false);
        this.dialog.set(null);
        this.toast.success(toastKey);
        this.list.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        const message = this.errors.message(err);
        if (inForm) {
          this.formError.set(message);
        } else {
          this.dialog.set(null);
          this.list.error.set(message);
        }
      },
    });
  }
}
