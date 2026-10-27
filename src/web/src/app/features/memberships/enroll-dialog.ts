import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Member, Membership, Plan } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { FcMoney } from '../../shared/format.pipes';
import { MemberPicker } from '../../shared/member-picker';
import { Modal } from '../../shared/modal';
import { ToastService } from '../../shared/toast';

/** Inscribe a un alumno en un plan: crea la membresía y su obligación de pago en una sola operación del servidor. */
@Component({
  selector: 'fc-enroll-dialog',
  imports: [ReactiveFormsModule, TranslocoPipe, Modal, MemberPicker, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="'memberships.new' | transloco" (closed)="closed.emit()">
      <form id="enroll-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <div class="field">
          <span>{{ 'memberships.member' | transloco }}</span>
          <fc-member-picker [initial]="member()" [locked]="!!member()" (picked)="selected.set($event)" />
        </div>
        <label class="field">
          <span>{{ 'memberships.plan' | transloco }}</span>
          <select formControlName="planId" (change)="onPlan()">
            <option value="">—</option>
            @for (plan of plans(); track plan.id) {
              <option [value]="plan.id">{{ plan.name }} · {{ plan.price | fcMoney }}</option>
            }
          </select>
        </label>
        @if (plan(); as p) {
          <p class="muted">
            {{ 'plans.subtotal' | transloco }} {{ p.subtotal | fcMoney }} · {{ 'plans.tax' | transloco: { percent: p.taxPercent } }} {{ p.taxAmount | fcMoney }}
          </p>
        }
        <div class="form-grid">
          <label class="field">
            <span>{{ 'memberships.startDate' | transloco }}</span>
            <input type="date" formControlName="startDate" />
            <small class="muted">{{ 'memberships.startDateHint' | transloco }}</small>
          </label>
          <label class="field">
            <span>{{ 'memberships.dueDate' | transloco }}</span>
            <input type="date" formControlName="dueDate" />
            <small class="muted">{{ 'memberships.dueDateHint' | transloco }}</small>
          </label>
        </div>
        @if (canApplyPromotion()) {
          <label class="field">
            <span>{{ 'memberships.promotionCode' | transloco }}</span>
            <input formControlName="promotionCode" maxlength="30" autocapitalize="characters" />
            <small class="muted">{{ 'memberships.promotionHint' | transloco }}</small>
          </label>
        }
      </form>
      <ng-container footer>
        <button type="button" class="btn" (click)="closed.emit()">{{ 'common.cancel' | transloco }}</button>
        <button type="submit" form="enroll-form" class="btn btn-primary" [disabled]="saving()">{{ 'memberships.new' | transloco }}</button>
      </ng-container>
    </fc-modal>
  `,
})
export class EnrollDialog implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly session = inject(SessionService);
  private readonly fb = inject(FormBuilder);

  /** Alumno ya elegido (al inscribir desde su ficha). */
  readonly member = input<Member | null>(null);
  readonly done = output<Membership>();
  readonly closed = output<void>();

  protected readonly plans = signal<Plan[]>([]);
  protected readonly selected = signal<Member | null>(null);
  protected readonly plan = signal<Plan | null>(null);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly canApplyPromotion = computed(() => this.session.has(Perm.PromotionsApply));

  protected readonly form = this.fb.nonNullable.group({
    planId: ['', [Validators.required]],
    startDate: [''],
    dueDate: [''],
    promotionCode: [''],
  });

  ngOnInit(): void {
    this.selected.set(this.member());
    this.api.plans({ isActive: true, pageSize: 100 }).subscribe({
      next: (page) => this.plans.set(page.items),
      error: (err: unknown) => this.formError.set(this.errors.message(err)),
    });
  }

  protected onPlan(): void {
    this.plan.set(this.plans().find((p) => p.id === this.form.controls.planId.value) ?? null);
  }

  protected save(): void {
    const member = this.selected();
    if (!member || this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.errors.text('memberships.invalidForm'));
      return;
    }

    const v = this.form.getRawValue();
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .enroll({
        memberId: member.id,
        planId: v.planId,
        startDate: v.startDate || null,
        dueDate: v.dueDate || null,
        promotionId: null,
        promotionCode: v.promotionCode.trim() || null,
      })
      .subscribe({
        next: (membership) => {
          this.saving.set(false);
          this.toast.success('memberships.enrolledToast');
          this.done.emit(membership);
        },
        error: (err: unknown) => {
          this.saving.set(false);
          this.formError.set(this.errors.message(err));
        },
      });
  }
}
