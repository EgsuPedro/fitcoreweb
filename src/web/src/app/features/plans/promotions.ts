import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Plan, Promotion } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, promotionTone } from '../../shared/badge';
import { FcDate } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

type Discount = 'Percentage' | 'FixedAmount';

@Component({
  selector: 'fc-promotions',
  imports: [ReactiveFormsModule, TranslocoPipe, Badge, Modal, Pager, FcDate],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'promotions.title' | transloco }}</h1>
      @if (canManage()) {
        <button type="button" class="btn btn-primary" (click)="open('new')">{{ 'promotions.new' | transloco }}</button>
      }
    </header>

    <div class="toolbar">
      <input type="search" class="search" [placeholder]="'common.search' | transloco" [attr.aria-label]="'common.search' | transloco" (input)="list.search($any($event.target).value)" />
      <select [attr.aria-label]="'common.status' | transloco" (change)="list.set({ status: $any($event.target).value })">
        <option value="">{{ 'common.all' | transloco }}</option>
        @for (status of statuses; track status) {
          <option [value]="status">{{ 'enums.promotionStatus.' + status | transloco }}</option>
        }
      </select>
    </div>

    @if (list.error()) {
      <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'promotions.name' | transloco }}</th>
            <th>{{ 'promotions.code' | transloco }}</th>
            <th>{{ 'promotions.value' | transloco }}</th>
            <th>{{ 'promotions.validFrom' | transloco }}</th>
            <th>{{ 'promotions.validUntil' | transloco }}</th>
            <th>{{ 'promotions.used' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (p of list.items(); track p.id) {
            <tr>
              <td><strong>{{ p.name }}</strong></td>
              <td>{{ p.code ?? '—' }}</td>
              <td>{{ p.discountType === 'Percentage' ? p.value + ' %' : '$ ' + p.value }}</td>
              <td>{{ p.validFrom | fcDate }}</td>
              <td>{{ p.validUntil | fcDate }}</td>
              <td>{{ p.usedCount }} / {{ p.maxUses ?? ('promotions.unlimited' | transloco) }}</td>
              <td><fc-badge [tone]="tone(p.status)">{{ 'enums.promotionStatus.' + p.status | transloco }}</fc-badge></td>
              <td class="actions">
                @if (canManage()) {
                  <button type="button" class="btn" (click)="open(p)">{{ 'common.edit' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="8" class="empty">{{ (list.loading() ? 'common.loading' : 'promotions.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @if (editing()) {
      <fc-modal [title]="(editing() === 'new' ? 'promotions.new' : 'promotions.edit') | transloco" (closed)="editing.set(null)">
        <form id="promo-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
          @if (formError()) {
            <p class="alert alert-danger" role="alert">{{ formError() }}</p>
          }
          <label class="field">
            <span>{{ 'promotions.name' | transloco }}</span>
            <input formControlName="name" maxlength="100" />
          </label>
          <label class="field">
            <span>{{ 'promotions.code' | transloco }}</span>
            <input formControlName="code" maxlength="30" autocapitalize="characters" />
            <small class="muted">{{ 'promotions.codeHint' | transloco }}</small>
          </label>
          <div class="form-grid">
            <label class="field">
              <span>{{ 'promotions.discountType' | transloco }}</span>
              <select formControlName="discountType">
                <option value="Percentage">{{ 'enums.discountType.Percentage' | transloco }}</option>
                <option value="FixedAmount">{{ 'enums.discountType.FixedAmount' | transloco }}</option>
              </select>
            </label>
            <label class="field">
              <span>{{ 'promotions.value' | transloco }}</span>
              <input type="number" min="0.01" step="0.01" formControlName="value" />
              @if (form.controls.discountType.value === 'Percentage') {
                <small class="muted">{{ 'promotions.percentHint' | transloco }}</small>
              }
            </label>
            <label class="field">
              <span>{{ 'promotions.validFrom' | transloco }}</span>
              <input type="date" formControlName="validFrom" />
            </label>
            <label class="field">
              <span>{{ 'promotions.validUntil' | transloco }}</span>
              <input type="date" formControlName="validUntil" />
            </label>
            <label class="field">
              <span>{{ 'promotions.maxUses' | transloco }}</span>
              <input type="number" min="1" step="1" formControlName="maxUses" />
              <small class="muted">{{ 'promotions.maxUsesHint' | transloco }}</small>
            </label>
            <label class="check">
              <input type="checkbox" formControlName="isActive" />
              <span>{{ 'common.active' | transloco }}</span>
            </label>
          </div>
          <label class="field">
            <span>{{ 'promotions.restrictions' | transloco }}</span>
            <textarea formControlName="restrictions" maxlength="500"></textarea>
          </label>
          <fieldset class="field">
            <legend>{{ 'promotions.plans' | transloco }}</legend>
            <small class="muted">{{ 'promotions.plansHint' | transloco }}</small>
            @for (plan of plans(); track plan.id) {
              <label class="check">
                <input type="checkbox" [checked]="selectedPlans().has(plan.id)" (change)="togglePlan(plan.id, $any($event.target).checked)" />
                <span>{{ plan.name }}</span>
              </label>
            }
          </fieldset>
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="editing.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="promo-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }
  `,
})
export class Promotions implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  protected readonly list = new ListState<Promotion>((query) => this.api.promotions(query), this.errors, inject(DestroyRef));
  protected readonly statuses = ['Active', 'Scheduled', 'Expired', 'Exhausted', 'Inactive'];
  protected readonly tone = promotionTone;
  protected readonly canManage = computed(() => this.session.has(Perm.PromotionsManage));
  protected readonly plans = signal<Plan[]>([]);
  protected readonly editing = signal<Promotion | 'new' | null>(null);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly selectedPlans = signal<Set<string>>(new Set());

  protected readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    code: ['', [Validators.pattern(/^$|^[A-Za-z0-9-]{3,30}$/)]],
    discountType: ['Percentage' as Discount],
    value: [10, [Validators.required, Validators.min(0.01)]],
    validFrom: ['', [Validators.required]],
    validUntil: ['', [Validators.required]],
    maxUses: [null as number | null],
    restrictions: [''],
    isActive: [true],
  });

  ngOnInit(): void {
    this.list.load();
    this.api.plans({ pageSize: 100 }).subscribe({ next: (page) => this.plans.set(page.items) });
  }

  protected open(target: Promotion | 'new'): void {
    this.formError.set(null);
    if (target === 'new') {
      this.form.reset();
      this.selectedPlans.set(new Set());
    } else {
      this.form.reset({
        name: target.name,
        code: target.code ?? '',
        discountType: target.discountType as Discount,
        value: target.value,
        validFrom: target.validFrom,
        validUntil: target.validUntil,
        maxUses: target.maxUses,
        restrictions: target.restrictions ?? '',
        isActive: target.isActive,
      });
      this.selectedPlans.set(new Set(target.planIds));
    }
    this.editing.set(target);
  }

  protected togglePlan(id: string, checked: boolean): void {
    const next = new Set(this.selectedPlans());
    if (checked) {
      next.add(id);
    } else {
      next.delete(id);
    }
    this.selectedPlans.set(next);
  }

  protected save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.errors.text('promotions.invalidForm'));
      return;
    }
    const target = this.editing();
    if (!target) {
      return;
    }
    const v = this.form.getRawValue();
    const body = {
      name: v.name.trim(),
      code: v.code.trim() || null,
      discountType: v.discountType,
      value: Number(v.value),
      validFrom: v.validFrom,
      validUntil: v.validUntil,
      maxUses: v.maxUses ? Number(v.maxUses) : null,
      restrictions: v.restrictions.trim() || null,
      isActive: v.isActive,
      planIds: [...this.selectedPlans()],
    };
    this.saving.set(true);
    this.formError.set(null);
    const request = target === 'new' ? this.api.createPromotion(body) : this.api.updatePromotion(target.id, body);
    request.subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(null);
        this.toast.success('promotions.savedToast');
        this.list.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }
}
