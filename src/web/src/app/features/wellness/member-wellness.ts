import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { AssessmentsPanel } from './assessments-panel';
import { GoalsPanel } from './goals-panel';
import { NutritionPanel } from './nutrition-panel';
import { PhotosPanel } from './photos-panel';
import { TrainingPanel } from './training-panel';

type Tab = 'training' | 'nutrition' | 'assessments' | 'goals' | 'photos';

const TABS: readonly { id: Tab; labelKey: string; permission: string }[] = [
  { id: 'training', labelKey: 'wellness.tabs.training', permission: Perm.TrainingMemberRead },
  { id: 'nutrition', labelKey: 'wellness.tabs.nutrition', permission: Perm.NutritionMemberRead },
  { id: 'assessments', labelKey: 'wellness.tabs.assessments', permission: Perm.AssessmentsRead },
  { id: 'goals', labelKey: 'wellness.tabs.goals', permission: Perm.GoalsRead },
  { id: 'photos', labelKey: 'wellness.tabs.photos', permission: Perm.PhotosRead },
];

/**
 * Entrenamiento, nutrición y progreso de un alumno. Se usa en la ficha del alumno (personal) y en el portal del propio alumno:
 * la API aplica las mismas reglas de acceso en ambos casos, y aquí solo se muestran las pestañas para las que hay permiso
 * (el propietario y el administrador no ven ninguna: son datos sensibles que no reciben por defecto).
 */
@Component({
  selector: 'fc-member-wellness',
  imports: [TranslocoPipe, TrainingPanel, NutritionPanel, AssessmentsPanel, GoalsPanel, PhotosPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (tabs().length > 0) {
      <section class="section">
        <h2>{{ (self() ? 'wellness.myTitle' : 'wellness.title') | transloco }}</h2>
        <div class="tabs" role="tablist" [attr.aria-label]="'wellness.title' | transloco">
          @for (t of tabs(); track t.id) {
            <button
              type="button"
              role="tab"
              class="tab"
              [id]="'wellness-tab-' + t.id"
              [attr.aria-selected]="current() === t.id"
              [attr.aria-controls]="'wellness-panel-' + t.id"
              [class.active]="current() === t.id"
              (click)="selected.set(t.id)"
            >
              {{ t.labelKey | transloco }}
            </button>
          }
        </div>
        <div role="tabpanel" [id]="'wellness-panel-' + current()" [attr.aria-labelledby]="'wellness-tab-' + current()">
          @switch (current()) {
            @case ('training') {
              <fc-training-panel [memberId]="memberId()" [self]="self()" />
            }
            @case ('nutrition') {
              <fc-nutrition-panel [memberId]="memberId()" [self]="self()" />
            }
            @case ('assessments') {
              <fc-assessments-panel [memberId]="memberId()" />
            }
            @case ('goals') {
              <fc-goals-panel [memberId]="memberId()" />
            }
            @case ('photos') {
              <fc-photos-panel [memberId]="memberId()" [self]="self()" />
            }
          }
        </div>
      </section>
    }
  `,
})
export class MemberWellness {
  private readonly session = inject(SessionService);

  readonly memberId = input.required<string>();
  /** Es el propio alumno quien mira (portal): él registra sus sesiones y comidas, y da su propio consentimiento. */
  readonly self = input(false);

  protected readonly selected = signal<Tab | null>(null);
  protected readonly tabs = computed(() => TABS.filter((t) => this.session.has(t.permission)));
  protected readonly current = computed<Tab>(() => {
    const chosen = this.selected();
    return this.tabs().some((t) => t.id === chosen) ? chosen! : (this.tabs()[0]?.id ?? 'training');
  });
}
