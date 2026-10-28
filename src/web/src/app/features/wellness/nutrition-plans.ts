import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { NutritionPlanDetail, NutritionPlanRequest, NutritionPlanSummary, NutritionPlanVersion } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDate } from '../../shared/format.pipes';
import { toInt, toNumber } from '../../shared/form-utils';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

interface ItemDraft {
  foodName: string;
  quantity: string;
  unit: string;
  calories: string;
  protein: string;
  carbs: string;
  fat: string;
  notes: string;
}

interface MealDraft {
  name: string;
  suggestedTime: string;
  notes: string;
  items: ItemDraft[];
}

interface PlanDraft {
  id: string | null;
  name: string;
  goal: string;
  description: string;
  calories: string;
  protein: string;
  carbs: string;
  fat: string;
  recommendations: string;
  validFrom: string;
  validUntil: string;
  changeNote: string;
  meals: MealDraft[];
}

const emptyItem = (): ItemDraft => ({ foodName: '', quantity: '1', unit: 'g', calories: '', protein: '', carbs: '', fat: '', notes: '' });

/**
 * Constructor de planes nutricionales. Como las rutinas, cada guardado de un plan existente crea una versión nueva y las
 * asignaciones en curso conservan la suya. Los valores nutricionales los escribe quien arma el plan: el sistema no calcula dietas.
 */
@Component({
  selector: 'fc-nutrition-plans',
  imports: [TranslocoPipe, Badge, FcDate, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'nutritionPlans.title' | transloco }}</h1>
      @if (canManage() && !draft()) {
        <button type="button" class="btn btn-primary" (click)="startNew()">{{ 'nutritionPlans.new' | transloco }}</button>
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
        <h2>{{ (d.id ? 'nutritionPlans.edit' : 'nutritionPlans.new') | transloco }}</h2>
        @if (d.id) {
          <p class="muted">{{ 'trainingPlans.versionNote' | transloco }}</p>
        }
        <div class="form-grid">
          <label class="field"><span>{{ 'trainingPlans.name' | transloco }}</span><input maxlength="120" [value]="d.name" (input)="patch('name', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'trainingPlans.goal' | transloco }}</span><input maxlength="200" [value]="d.goal" (input)="patch('goal', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'nutritionPlans.calories' | transloco }} (kcal)</span><input inputmode="numeric" [value]="d.calories" (input)="patch('calories', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'nutrition.protein' | transloco }} (g)</span><input inputmode="numeric" [value]="d.protein" (input)="patch('protein', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'nutrition.carbs' | transloco }} (g)</span><input inputmode="numeric" [value]="d.carbs" (input)="patch('carbs', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'nutrition.fat' | transloco }} (g)</span><input inputmode="numeric" [value]="d.fat" (input)="patch('fat', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'trainingPlans.validFrom' | transloco }}</span><input type="date" [value]="d.validFrom" (input)="patch('validFrom', $any($event.target).value)" /></label>
          <label class="field"><span>{{ 'trainingPlans.validUntil' | transloco }}</span><input type="date" [value]="d.validUntil" (input)="patch('validUntil', $any($event.target).value)" /></label>
        </div>
        <label class="field"><span>{{ 'exercises.description' | transloco }}</span><textarea maxlength="1000" [value]="d.description" (input)="patch('description', $any($event.target).value)"></textarea></label>
        <label class="field"><span>{{ 'nutritionPlans.recommendations' | transloco }}</span><textarea maxlength="2000" [value]="d.recommendations" (input)="patch('recommendations', $any($event.target).value)"></textarea></label>
        @if (d.id) {
          <label class="field"><span>{{ 'trainingPlans.changeNote' | transloco }}</span><input maxlength="300" [value]="d.changeNote" (input)="patch('changeNote', $any($event.target).value)" /></label>
        }

        @for (meal of d.meals; track $index; let i = $index) {
          <fieldset class="field subcard">
            <legend>{{ 'nutritionPlans.meal' | transloco: { n: i + 1 } }}</legend>
            <div class="row-form">
              <label class="field"><span>{{ 'trainingPlans.dayName' | transloco }}</span><input maxlength="80" [value]="meal.name" (input)="patchMeal(i, 'name', $any($event.target).value)" /></label>
              <label class="field"><span>{{ 'nutritionPlans.time' | transloco }}</span><input type="time" [value]="meal.suggestedTime" (input)="patchMeal(i, 'suggestedTime', $any($event.target).value)" /></label>
              <label class="field"><span>{{ 'common.notes' | transloco }}</span><input maxlength="300" [value]="meal.notes" (input)="patchMeal(i, 'notes', $any($event.target).value)" /></label>
              <span class="inline-actions">
                <button type="button" class="btn btn-ghost" [disabled]="i === 0" [attr.aria-label]="'trainingPlans.moveUp' | transloco" (click)="moveMeal(i, -1)">↑</button>
                <button type="button" class="btn btn-ghost" [disabled]="i === d.meals.length - 1" [attr.aria-label]="'trainingPlans.moveDown' | transloco" (click)="moveMeal(i, 1)">↓</button>
                <button type="button" class="btn btn-ghost" (click)="removeMeal(i)">{{ 'nutritionPlans.removeMeal' | transloco }}</button>
              </span>
            </div>
            @for (item of meal.items; track $index; let j = $index) {
              <div class="row-form">
                <label class="field"><span>{{ 'nutritionPlans.food' | transloco }}</span><input maxlength="120" [value]="item.foodName" (input)="patchItem(i, j, 'foodName', $any($event.target).value)" /></label>
                <label class="field"><span>{{ 'nutritionPlans.quantity' | transloco }}</span><input inputmode="decimal" [value]="item.quantity" (input)="patchItem(i, j, 'quantity', $any($event.target).value)" /></label>
                <label class="field"><span>{{ 'nutritionPlans.unit' | transloco }}</span><input maxlength="20" [value]="item.unit" (input)="patchItem(i, j, 'unit', $any($event.target).value)" /></label>
                <label class="field"><span>kcal</span><input inputmode="numeric" [value]="item.calories" (input)="patchItem(i, j, 'calories', $any($event.target).value)" /></label>
                <label class="field"><span>{{ 'nutrition.protein' | transloco }} (g)</span><input inputmode="decimal" [value]="item.protein" (input)="patchItem(i, j, 'protein', $any($event.target).value)" /></label>
                <label class="field"><span>{{ 'nutrition.carbs' | transloco }} (g)</span><input inputmode="decimal" [value]="item.carbs" (input)="patchItem(i, j, 'carbs', $any($event.target).value)" /></label>
                <label class="field"><span>{{ 'nutrition.fat' | transloco }} (g)</span><input inputmode="decimal" [value]="item.fat" (input)="patchItem(i, j, 'fat', $any($event.target).value)" /></label>
                <button type="button" class="btn btn-ghost" [attr.aria-label]="'common.delete' | transloco" (click)="removeItem(i, j)">×</button>
              </div>
            }
            <button type="button" class="btn" (click)="addItem(i)">{{ 'nutritionPlans.addItem' | transloco }}</button>
          </fieldset>
        }
        <div class="inline-actions"><button type="button" class="btn" (click)="addMeal()">{{ 'nutritionPlans.addMeal' | transloco }}</button></div>

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
              <th class="num">{{ 'nutritionPlans.calories' | transloco }}</th>
              <th class="num">{{ 'nutritionPlans.meals' | transloco }}</th>
              <th class="num">{{ 'trainingPlans.assigned' | transloco }}</th>
              <th>{{ 'common.status' | transloco }}</th>
              <th><span class="sr-only">{{ 'common.actions' | transloco }}</span></th>
            </tr>
          </thead>
          <tbody>
            @for (p of list.items(); track p.id) {
              <tr>
                <td>{{ p.name }} <small class="muted">v{{ p.version }}</small></td>
                <td class="num">{{ p.targetCalories ?? '—' }}</td>
                <td class="num">{{ p.mealCount }}</td>
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
              <tr><td colspan="6" class="empty">{{ 'nutritionPlans.empty' | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>
      <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
    }
  `,
})
export class NutritionPlans implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  protected readonly list = new ListState<NutritionPlanSummary>((query) => this.api.nutritionPlans(query), this.errors, inject(DestroyRef));
  protected readonly draft = signal<PlanDraft | null>(null);
  protected readonly versions = signal<NutritionPlanVersion[]>([]);
  protected readonly notice = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly canManage = computed(() => this.session.has(Perm.NutritionPlansManage));

  ngOnInit(): void {
    this.list.load();
  }

  protected startNew(): void {
    this.formError.set(null);
    this.versions.set([]);
    this.draft.set({
      id: null, name: '', goal: '', description: '', calories: '', protein: '', carbs: '', fat: '', recommendations: '', validFrom: '', validUntil: '', changeNote: '',
      meals: [{ name: '', suggestedTime: '', notes: '', items: [emptyItem()] }],
    });
  }

  protected open(summary: NutritionPlanSummary): void {
    this.formError.set(null);
    this.api.nutritionPlan(summary.id).subscribe({
      next: (plan) => {
        this.draft.set(this.toDraft(plan));
        this.api.nutritionPlanVersions(plan.id).subscribe({ next: (v) => this.versions.set(v), error: () => this.versions.set([]) });
      },
      error: (err: unknown) => this.notice.set(this.errors.message(err)),
    });
  }

  protected duplicate(summary: NutritionPlanSummary): void {
    this.api.duplicateNutritionPlan(summary.id).subscribe({
      next: () => {
        this.toast.success('trainingPlans.duplicatedToast');
        this.list.load();
      },
      error: (err: unknown) => this.notice.set(this.errors.message(err)),
    });
  }

  protected toggle(summary: NutritionPlanSummary): void {
    this.api.setNutritionPlanStatus(summary.id, summary.status !== 'Active').subscribe({
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

  protected addMeal(): void {
    this.draft.update((d) => (d ? { ...d, meals: [...d.meals, { name: '', suggestedTime: '', notes: '', items: [emptyItem()] }] } : d));
  }

  protected removeMeal(i: number): void {
    this.draft.update((d) => (d ? { ...d, meals: d.meals.filter((_, k) => k !== i) } : d));
  }

  protected moveMeal(i: number, delta: number): void {
    this.draft.update((d) => {
      if (!d || i + delta < 0 || i + delta >= d.meals.length) {
        return d;
      }
      const meals = [...d.meals];
      [meals[i], meals[i + delta]] = [meals[i + delta], meals[i]];
      return { ...d, meals };
    });
  }

  protected patchMeal(i: number, key: 'name' | 'suggestedTime' | 'notes', value: string): void {
    this.mapMeal(i, (meal) => ({ ...meal, [key]: value }));
  }

  protected addItem(i: number): void {
    this.mapMeal(i, (meal) => ({ ...meal, items: [...meal.items, emptyItem()] }));
  }

  protected removeItem(i: number, j: number): void {
    this.mapMeal(i, (meal) => ({ ...meal, items: meal.items.filter((_, k) => k !== j) }));
  }

  protected patchItem(i: number, j: number, key: keyof ItemDraft, value: string): void {
    this.mapMeal(i, (meal) => ({ ...meal, items: meal.items.map((item, k) => (k === j ? { ...item, [key]: value } : item)) }));
  }

  protected save(): void {
    const d = this.draft();
    if (!d) {
      return;
    }
    const body: NutritionPlanRequest = {
      name: d.name.trim(),
      goal: d.goal.trim(),
      description: d.description.trim() || null,
      targetCalories: toInt(d.calories),
      targetProteinG: toInt(d.protein),
      targetCarbsG: toInt(d.carbs),
      targetFatG: toInt(d.fat),
      recommendations: d.recommendations.trim() || null,
      validFrom: d.validFrom || null,
      validUntil: d.validUntil || null,
      changeNote: d.id ? d.changeNote.trim() : '',
      meals: d.meals.map((meal) => ({
        name: meal.name.trim(),
        suggestedTime: meal.suggestedTime ? `${meal.suggestedTime}:00` : null,
        notes: meal.notes.trim() || null,
        items: meal.items.map((item) => ({
          foodName: item.foodName.trim(),
          quantity: toNumber(item.quantity) ?? 0,
          unit: item.unit.trim(),
          calories: toInt(item.calories),
          proteinG: toNumber(item.protein),
          carbsG: toNumber(item.carbs),
          fatG: toNumber(item.fat),
          notes: item.notes.trim() || null,
        })),
      })),
    };
    this.busy.set(true);
    this.formError.set(null);
    (d.id ? this.api.updateNutritionPlan(d.id, body) : this.api.createNutritionPlan(body)).subscribe({
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

  private toDraft(plan: NutritionPlanDetail): PlanDraft {
    return {
      id: plan.id,
      name: plan.name,
      goal: plan.goal ?? '',
      description: plan.description ?? '',
      calories: plan.targetCalories?.toString() ?? '',
      protein: plan.targetProteinG?.toString() ?? '',
      carbs: plan.targetCarbsG?.toString() ?? '',
      fat: plan.targetFatG?.toString() ?? '',
      recommendations: plan.recommendations ?? '',
      validFrom: plan.validFrom ?? '',
      validUntil: plan.validUntil ?? '',
      changeNote: '',
      meals: plan.meals.map((meal) => ({
        name: meal.name,
        suggestedTime: meal.suggestedTime?.slice(0, 5) ?? '',
        notes: meal.notes ?? '',
        items: meal.items.map((item) => ({
          foodName: item.foodName,
          quantity: String(item.quantity),
          unit: item.unit,
          calories: item.calories?.toString() ?? '',
          protein: item.proteinG?.toString() ?? '',
          carbs: item.carbsG?.toString() ?? '',
          fat: item.fatG?.toString() ?? '',
          notes: item.notes ?? '',
        })),
      })),
    };
  }

  private mapMeal(i: number, change: (meal: MealDraft) => MealDraft): void {
    this.draft.update((d) => (d ? { ...d, meals: d.meals.map((meal, k) => (k === i ? change(meal) : meal)) } : d));
  }
}
