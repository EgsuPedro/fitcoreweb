import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Plan } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { FcDate, FcMoney } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

type Unit = 'Day' | 'Week' | 'Month' | 'Year';
type Tax = 'General' | 'Zero' | 'NotSubject' | 'Exempt';

@Component({
  selector: 'fc-plans',
  imports: [ReactiveFormsModule, TranslocoPipe, Badge, Modal, Pager, FcMoney, FcDate],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'plans.title' | transloco }}</h1>
      @if (canManage()) {
        <button type="button" class="btn btn-primary" (click)="open('new')">{{ 'plans.new' | transloco }}</button>
      }
    </header>

    <div class="toolbar">
      <input type="search" class="search" [placeholder]="'common.search' | transloco" [attr.aria-label]="'common.search' | transloco" (input)="list.search($any($event.target).value)" />
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
            <th>{{ 'plans.name' | transloco }}</th>
            <th>{{ 'plans.duration' | transloco }}</th>
            <th class="num">{{ 'plans.price' | transloco }}</th>
            <th>{{ 'plans.taxKind' | transloco }}</th>
            <th>{{ 'plans.validUntil' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (plan of list.items(); track plan.id) {
            <tr>
              <td><strong>{{ plan.name }}</strong></td>
              <td>{{ plan.durationValue }} {{ 'enums.durationUnit.' + plan.durationUnit | transloco }}</td>
              <td class="num">{{ plan.price | fcMoney }}</td>
              <td>{{ 'enums.taxKind.' + plan.taxKind | transloco }} @if (plan.taxKind === 'General') { ({{ plan.taxPercent }} %) }</td>
              <td>{{ plan.validUntil | fcDate }}</td>
              <td><fc-badge [tone]="plan.isActive ? 'ok' : 'off'">{{ (plan.isActive ? 'common.active' : 'common.inactive') | transloco }}</fc-badge></td>
              <td class="actions">
                @if (canManage()) {
                  <button type="button" class="btn" (click)="open(plan)">{{ 'common.edit' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="7" class="empty">{{ (list.loading() ? 'common.loading' : 'plans.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @if (editing()) {
      <fc-modal [title]="(editing() === 'new' ? 'plans.new' : 'plans.edit') | transloco" (closed)="editing.set(null)">
        <form id="plan-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
          @if (formError()) {
            <p class="alert alert-danger" role="alert">{{ formError() }}</p>
          }
          @if (editing() !== 'new') {
            <p class="muted">{{ 'plans.editNote' | transloco }}</p>
          }
          <label class="field">
            <span>{{ 'plans.name' | transloco }}</span>
            <input formControlName="name" maxlength="100" />
          </label>
          <label class="field">
            <span>{{ 'plans.description' | transloco }}</span>
            <textarea formControlName="description" maxlength="500"></textarea>
          </label>
          <div class="form-grid">
            <label class="field">
              <span>{{ 'plans.price' | transloco }}</span>
              <input type="number" min="0" step="0.01" formControlName="price" />
              <small class="muted">{{ 'plans.priceHint' | transloco }}</small>
            </label>
            <label class="field">
              <span>{{ 'plans.taxKind' | transloco }}</span>
              <select formControlName="taxKind">
                @for (kind of taxKinds; track kind) {
                  <option [value]="kind">{{ 'enums.taxKind.' + kind | transloco }}</option>
                }
              </select>
            </label>
            <label class="field">
              <span>{{ 'plans.durationValue' | transloco }}</span>
              <input type="number" min="1" max="999" step="1" formControlName="durationValue" />
            </label>
            <label class="field">
              <span>{{ 'plans.unit' | transloco }}</span>
              <select formControlName="durationUnit">
                @for (unit of units; track unit) {
                  <option [value]="unit">{{ 'enums.durationUnit.' + unit | transloco }}</option>
                }
              </select>
            </label>
            <label class="field">
              <span>{{ 'plans.validFrom' | transloco }}</span>
              <input type="date" formControlName="validFrom" />
            </label>
            <label class="field">
              <span>{{ 'plans.validUntil' | transloco }}</span>
              <input type="date" formControlName="validUntil" />
            </label>
          </div>
          <label class="field">
            <span>{{ 'plans.access' | transloco }}</span>
            <input formControlName="allowedAccess" maxlength="300" />
          </label>
          <label class="field">
            <span>{{ 'plans.restrictions' | transloco }}</span>
            <textarea formControlName="restrictions" maxlength="500"></textarea>
          </label>
          <label class="field">
            <span>{{ 'plans.terms' | transloco }}</span>
            <textarea formControlName="commercialTerms" maxlength="1000"></textarea>
          </label>
          <div class="form-grid">
            <label class="check">
              <input type="checkbox" formControlName="allowFreeze" />
              <span>{{ 'plans.allowFreeze' | transloco }}</span>
            </label>
            @if (form.controls.allowFreeze.value) {
              <label class="field">
                <span>{{ 'plans.maxFreezeDays' | transloco }}</span>
                <input type="number" min="1" max="365" step="1" formControlName="maxFreezeDays" />
              </label>
            }
            <label class="check">
              <input type="checkbox" formControlName="isActive" />
              <span>{{ 'common.active' | transloco }}</span>
            </label>
          </div>
          <fieldset class="field">
            <legend>{{ 'plans.branches' | transloco }}</legend>
            <small class="muted">{{ 'plans.branchesHint' | transloco }}</small>
            @for (branch of branches(); track branch.id) {
              <label class="check">
                <input type="checkbox" [checked]="selectedBranches().has(branch.id)" (change)="toggleBranch(branch.id, $any($event.target).checked)" />
                <span>{{ branch.name }}</span>
              </label>
            }
          </fieldset>
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="editing.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="plan-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }
  `,
})
export class Plans implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);
  private readonly catalog = inject(BranchCatalog);

  protected readonly list = new ListState<Plan>((query) => this.api.plans(query), this.errors, inject(DestroyRef));
  protected readonly branches = this.catalog.branches;
  protected readonly units: Unit[] = ['Day', 'Week', 'Month', 'Year'];
  protected readonly taxKinds: Tax[] = ['General', 'Zero', 'NotSubject', 'Exempt'];
  protected readonly canManage = computed(() => this.session.has(Perm.PlansManage));
  protected readonly editing = signal<Plan | 'new' | null>(null);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly selectedBranches = signal<Set<string>>(new Set());

  protected readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    description: [''],
    price: [0, [Validators.required, Validators.min(0)]],
    taxKind: ['General' as Tax],
    durationValue: [1, [Validators.required, Validators.min(1), Validators.max(999)]],
    durationUnit: ['Month' as Unit],
    validFrom: [''],
    validUntil: [''],
    allowedAccess: [''],
    restrictions: [''],
    commercialTerms: [''],
    allowFreeze: [false],
    maxFreezeDays: [0],
    isActive: [true],
  });

  ngOnInit(): void {
    this.catalog.ensure();
    this.list.load();
  }

  protected open(target: Plan | 'new'): void {
    this.formError.set(null);
    if (target === 'new') {
      this.form.reset();
      this.selectedBranches.set(new Set());
    } else {
      this.form.reset({
        name: target.name,
        description: target.description ?? '',
        price: target.price,
        taxKind: target.taxKind as Tax,
        durationValue: target.durationValue,
        durationUnit: target.durationUnit as Unit,
        validFrom: target.validFrom ?? '',
        validUntil: target.validUntil ?? '',
        allowedAccess: target.allowedAccess ?? '',
        restrictions: target.restrictions ?? '',
        commercialTerms: target.commercialTerms ?? '',
        allowFreeze: target.allowFreeze,
        maxFreezeDays: target.maxFreezeDays,
        isActive: target.isActive,
      });
      this.selectedBranches.set(new Set(target.branchIds));
    }
    this.editing.set(target);
  }

  protected toggleBranch(id: string, checked: boolean): void {
    const next = new Set(this.selectedBranches());
    if (checked) {
      next.add(id);
    } else {
      next.delete(id);
    }
    this.selectedBranches.set(next);
  }

  protected save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.errors.text('plans.invalidForm'));
      return;
    }
    const target = this.editing();
    if (!target) {
      return;
    }
    const v = this.form.getRawValue();
    const nullable = (text: string): string | null => text.trim() || null;
    const body = {
      name: v.name.trim(),
      description: nullable(v.description),
      price: Number(v.price),
      durationValue: Number(v.durationValue),
      durationUnit: v.durationUnit,
      allowedAccess: nullable(v.allowedAccess),
      restrictions: nullable(v.restrictions),
      commercialTerms: nullable(v.commercialTerms),
      taxKind: v.taxKind,
      isActive: v.isActive,
      validFrom: v.validFrom || null,
      validUntil: v.validUntil || null,
      allowFreeze: v.allowFreeze,
      maxFreezeDays: v.allowFreeze ? Number(v.maxFreezeDays) : 0,
      branchIds: [...this.selectedBranches()],
    };
    this.saving.set(true);
    this.formError.set(null);
    const request = target === 'new' ? this.api.createPlan(body) : this.api.updatePlan(target.id, body);
    request.subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(null);
        this.toast.success('plans.savedToast');
        this.list.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }
}
