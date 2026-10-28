import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { ActivityType, ClassLevel, GroupClass, Trainer } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { Confirm } from '../../shared/confirm';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

export const ACTIVITIES: readonly ActivityType[] = ['Yoga', 'Spinning', 'Functional', 'Zumba', 'Pilates', 'Stretching', 'Boxing', 'Dance', 'Other'];
export const LEVELS: readonly ClassLevel[] = ['AllLevels', 'Beginner', 'Intermediate', 'Advanced'];

/** Clases grupales: qué se ofrece y con qué reglas. Cada fecha concreta es una sesión del horario. */
@Component({
  selector: 'fc-classes',
  imports: [ReactiveFormsModule, TranslocoPipe, Badge, Modal, Pager, Confirm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'classes.title' | transloco }}</h1>
      @if (canManage()) {
        <button type="button" class="btn btn-primary" (click)="openCreate()">{{ 'classes.new' | transloco }}</button>
      }
    </header>

    <div class="toolbar">
      <input type="search" class="search" [placeholder]="'common.search' | transloco" [attr.aria-label]="'common.search' | transloco" (input)="list.search($any($event.target).value)" />
      <select [attr.aria-label]="'classes.activity' | transloco" (change)="list.set({ activity: $any($event.target).value })">
        <option value="">{{ 'classes.allActivities' | transloco }}</option>
        @for (a of activities; track a) {
          <option [value]="a">{{ 'enums.activityType.' + a | transloco }}</option>
        }
      </select>
      <select [attr.aria-label]="'common.branch' | transloco" (change)="list.set({ branchId: $any($event.target).value })">
        <option value="">{{ 'common.allBranches' | transloco }}</option>
        @for (branch of catalog.branches(); track branch.id) {
          <option [value]="branch.id">{{ branch.name }}</option>
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
            <th>{{ 'classes.name' | transloco }}</th>
            <th>{{ 'classes.activity' | transloco }}</th>
            <th>{{ 'classes.instructor' | transloco }}</th>
            <th>{{ 'common.branch' | transloco }}</th>
            <th class="num">{{ 'classes.duration' | transloco }}</th>
            <th class="num">{{ 'classes.capacity' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (c of list.items(); track c.id) {
            <tr>
              <td>
                <strong>{{ c.name }}</strong><br />
                <small class="muted">{{ 'enums.classLevel.' + c.level | transloco }}</small>
              </td>
              <td>{{ 'enums.activityType.' + c.activity | transloco }}</td>
              <td>{{ c.instructorName }}</td>
              <td>{{ c.branchName }}</td>
              <td class="num">{{ c.durationMinutes }} min</td>
              <td class="num">{{ c.capacity }}</td>
              <td><fc-badge [tone]="c.isActive ? 'ok' : 'off'">{{ (c.isActive ? 'common.active' : 'common.inactive') | transloco }}</fc-badge></td>
              <td class="actions">
                @if (canManage()) {
                  <button type="button" class="btn" (click)="openEdit(c)">{{ 'common.edit' | transloco }}</button>
                  <button type="button" class="btn" (click)="toggling.set(c)">{{ (c.isActive ? 'classes.deactivate' : 'classes.activate') | transloco }}</button>
                } @else {
                  <button type="button" class="btn" (click)="openEdit(c)">{{ 'common.view' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="8" class="empty">{{ (list.loading() ? 'common.loading' : 'classes.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @if (editing() !== null) {
      <fc-modal [title]="(editing()?.id ? 'classes.edit' : 'classes.new') | transloco" (closed)="editing.set(null)">
        <form id="class-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
          @if (formError()) {
            <p class="alert alert-danger" role="alert">{{ formError() }}</p>
          }
          <fieldset [disabled]="!canManage()" class="plain">
            <div class="grid-2">
              <label class="field">
                <span>{{ 'common.branch' | transloco }}</span>
                <select formControlName="branchId" (change)="loadInstructors()">
                  <option value="">—</option>
                  @for (branch of catalog.branches(); track branch.id) {
                    <option [value]="branch.id">{{ branch.name }}</option>
                  }
                </select>
                @if (editing()?.id) {
                  <small class="muted">{{ 'classes.branchLocked' | transloco }}</small>
                }
              </label>
              <label class="field">
                <span>{{ 'classes.name' | transloco }}</span>
                <input formControlName="name" maxlength="120" />
              </label>
              <label class="field">
                <span>{{ 'classes.activity' | transloco }}</span>
                <select formControlName="activity">
                  @for (a of activities; track a) {
                    <option [value]="a">{{ 'enums.activityType.' + a | transloco }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span>{{ 'classes.level' | transloco }}</span>
                <select formControlName="level">
                  @for (l of levels; track l) {
                    <option [value]="l">{{ 'enums.classLevel.' + l | transloco }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span>{{ 'classes.instructor' | transloco }}</span>
                <select formControlName="instructorId">
                  <option value="">—</option>
                  @for (t of instructors(); track t.id) {
                    <option [value]="t.id">{{ t.fullName }}</option>
                  }
                </select>
                <small class="muted">{{ 'classes.instructorHint' | transloco }}</small>
              </label>
              <label class="field">
                <span>{{ 'classes.duration' | transloco }} (min)</span>
                <input type="number" formControlName="durationMinutes" min="5" max="480" />
              </label>
              <label class="field">
                <span>{{ 'classes.capacity' | transloco }}</span>
                <input type="number" formControlName="capacity" min="1" max="500" />
              </label>
              <label class="field">
                <span>{{ 'classes.minAge' | transloco }}</span>
                <input type="number" formControlName="minAge" min="0" max="100" />
                <small class="muted">{{ 'classes.minAgeHint' | transloco }}</small>
              </label>
            </div>
            <label class="field">
              <span>{{ 'classes.description' | transloco }}</span>
              <textarea formControlName="description" maxlength="1000"></textarea>
            </label>
            <label class="field">
              <span>{{ 'classes.requirements' | transloco }}</span>
              <input formControlName="requirements" maxlength="500" />
              <small class="muted">{{ 'classes.requirementsHint' | transloco }}</small>
            </label>

            <h3>{{ 'classes.bookingRules' | transloco }}</h3>
            <div class="grid-2">
              <label class="field">
                <span>{{ 'classes.minNotice' | transloco }}</span>
                <input type="number" formControlName="minBookingNoticeMinutes" min="0" max="10080" />
                <small class="muted">{{ 'classes.minNoticeHint' | transloco }}</small>
              </label>
              <label class="field">
                <span>{{ 'classes.maxAdvance' | transloco }}</span>
                <input type="number" formControlName="maxBookingAdvanceDays" min="1" max="365" />
                <small class="muted">{{ 'classes.maxAdvanceHint' | transloco }}</small>
              </label>
              <label class="field">
                <span>{{ 'classes.cancellation' | transloco }}</span>
                <input type="number" formControlName="cancellationDeadlineMinutes" min="0" max="10080" />
                <small class="muted">{{ 'classes.cancellationHint' | transloco }}</small>
              </label>
            </div>

            <h3>{{ 'classes.eligibility' | transloco }}</h3>
            <label class="check">
              <input type="checkbox" formControlName="requireActiveMembership" />
              <span>{{ 'classes.requireMembership' | transloco }}</span>
            </label>
            <label class="check">
              <input type="checkbox" formControlName="applyPaymentPolicy" />
              <span>{{ 'classes.applyPaymentPolicy' | transloco }}</span>
            </label>
            @if (form.controls.applyPaymentPolicy.value && !form.controls.requireActiveMembership.value) {
              <p class="alert alert-warning">{{ 'classes.paymentNeedsMembership' | transloco }}</p>
            }

            <h3>{{ 'classes.waitlist' | transloco }}</h3>
            <label class="check">
              <input type="checkbox" formControlName="waitlistEnabled" />
              <span>{{ 'classes.waitlistEnabled' | transloco }}</span>
            </label>
            @if (form.controls.waitlistEnabled.value) {
              <div class="grid-2">
                <label class="field">
                  <span>{{ 'classes.waitlistCapacity' | transloco }}</span>
                  <input type="number" formControlName="waitlistCapacity" min="1" max="200" />
                </label>
                <label class="field">
                  <span>{{ 'classes.offerWindow' | transloco }}</span>
                  <input type="number" formControlName="offerWindowMinutes" min="5" max="1440" />
                  <small class="muted">{{ 'classes.offerWindowHint' | transloco }}</small>
                </label>
              </div>
            }
          </fieldset>
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="editing.set(null)">{{ 'common.close' | transloco }}</button>
          @if (canManage()) {
            <button type="submit" form="class-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
          }
        </ng-container>
      </fc-modal>
    }

    @if (toggling(); as target) {
      <fc-confirm
        [title]="(target.isActive ? 'classes.deactivate' : 'classes.activate') | transloco"
        [message]="(target.isActive ? 'classes.deactivateConfirm' : 'classes.activateConfirm') | transloco: { name: target.name }"
        [confirmLabel]="(target.isActive ? 'classes.deactivate' : 'classes.activate') | transloco"
        [danger]="target.isActive"
        (confirmed)="toggle(target)"
        (cancelled)="toggling.set(null)"
      />
    }
  `,
})
export class Classes implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly activities = ACTIVITIES;
  protected readonly levels = LEVELS;
  protected readonly list = new ListState<GroupClass>((query) => this.api.groupClasses(query), this.errors, inject(DestroyRef));
  protected readonly canManage = computed(() => this.session.has(Perm.GroupClassesManage));
  /** Clase en edición (con `id`) o `{}` al crear; null = sin diálogo. */
  protected readonly editing = signal<Partial<GroupClass> | null>(null);
  protected readonly toggling = signal<GroupClass | null>(null);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly instructors = signal<Trainer[]>([]);

  protected readonly form = this.fb.nonNullable.group({
    branchId: ['', [Validators.required]],
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(120)]],
    description: [''],
    activity: ['Yoga' as ActivityType],
    level: ['AllLevels' as ClassLevel],
    instructorId: ['', [Validators.required]],
    durationMinutes: [60, [Validators.required, Validators.min(5), Validators.max(480)]],
    capacity: [20, [Validators.required, Validators.min(1), Validators.max(500)]],
    requirements: [''],
    minBookingNoticeMinutes: [30, [Validators.required, Validators.min(0), Validators.max(10080)]],
    maxBookingAdvanceDays: [14, [Validators.required, Validators.min(1), Validators.max(365)]],
    cancellationDeadlineMinutes: [120, [Validators.required, Validators.min(0), Validators.max(10080)]],
    waitlistEnabled: [true],
    waitlistCapacity: [5],
    offerWindowMinutes: [30],
    requireActiveMembership: [true],
    applyPaymentPolicy: [true],
    minAge: [null as number | null],
  });

  ngOnInit(): void {
    this.catalog.ensure();
    this.list.load();
  }

  protected openCreate(): void {
    this.formError.set(null);
    this.form.reset();
    this.form.controls.branchId.enable();
    this.instructors.set([]);
    this.editing.set({});
  }

  protected openEdit(groupClass: GroupClass): void {
    this.formError.set(null);
    this.form.reset({
      branchId: groupClass.branchId,
      name: groupClass.name,
      description: groupClass.description ?? '',
      activity: groupClass.activity as ActivityType,
      level: groupClass.level as ClassLevel,
      instructorId: groupClass.instructorId,
      durationMinutes: groupClass.durationMinutes,
      capacity: groupClass.capacity,
      requirements: groupClass.requirements ?? '',
      minBookingNoticeMinutes: groupClass.minBookingNoticeMinutes,
      maxBookingAdvanceDays: groupClass.maxBookingAdvanceDays,
      cancellationDeadlineMinutes: groupClass.cancellationDeadlineMinutes,
      waitlistEnabled: groupClass.waitlistEnabled,
      waitlistCapacity: groupClass.waitlistEnabled ? groupClass.waitlistCapacity : 5,
      offerWindowMinutes: groupClass.offerWindowMinutes,
      requireActiveMembership: groupClass.requireActiveMembership,
      applyPaymentPolicy: groupClass.applyPaymentPolicy,
      minAge: groupClass.minAge,
    });
    // La sucursal de una clase no cambia: para otra, se crea otra clase.
    this.form.controls.branchId.disable();
    this.editing.set(groupClass);
    this.loadInstructors(groupClass.instructorId);
  }

  /** Entrenadores activos de la sucursal elegida (el servidor valida de nuevo que el instructor trabaje en ella). */
  protected loadInstructors(keep?: string): void {
    const branchId = this.form.controls.branchId.value;
    if (!branchId) {
      this.instructors.set([]);
      return;
    }
    this.api.trainers({ pageSize: 100, status: 'Active', branchId }).subscribe({
      next: (page) => {
        this.instructors.set(page.items);
        if (!keep && !page.items.some((t) => t.id === this.form.controls.instructorId.value)) {
          this.form.controls.instructorId.setValue('');
        }
      },
      error: (err: unknown) => this.formError.set(this.errors.message(err)),
    });
  }

  protected save(): void {
    if (this.form.invalid || (this.form.controls.applyPaymentPolicy.value && !this.form.controls.requireActiveMembership.value)) {
      this.form.markAllAsTouched();
      this.formError.set(this.errors.text('classes.invalidForm'));
      return;
    }
    const v = this.form.getRawValue();
    const body = {
      branchId: v.branchId,
      name: v.name.trim(),
      description: v.description.trim() || null,
      activity: v.activity,
      instructorId: v.instructorId,
      durationMinutes: Number(v.durationMinutes),
      capacity: Number(v.capacity),
      level: v.level,
      requirements: v.requirements.trim() || null,
      minBookingNoticeMinutes: Number(v.minBookingNoticeMinutes),
      maxBookingAdvanceDays: Number(v.maxBookingAdvanceDays),
      cancellationDeadlineMinutes: Number(v.cancellationDeadlineMinutes),
      waitlistEnabled: v.waitlistEnabled,
      // Sin lista de espera su capacidad es 0 (el servidor lo exige).
      waitlistCapacity: v.waitlistEnabled ? Number(v.waitlistCapacity) : 0,
      offerWindowMinutes: Number(v.offerWindowMinutes) || 30,
      requireActiveMembership: v.requireActiveMembership,
      applyPaymentPolicy: v.applyPaymentPolicy,
      minAge: v.minAge === null || v.minAge === undefined || String(v.minAge) === '' ? null : Number(v.minAge),
    };
    const id = this.editing()?.id;
    this.saving.set(true);
    this.formError.set(null);
    (id ? this.api.updateGroupClass(id, body) : this.api.createGroupClass(body)).subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(null);
        this.toast.success(id ? 'classes.updatedToast' : 'classes.createdToast');
        this.list.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  protected toggle(groupClass: GroupClass): void {
    this.api.setGroupClassStatus(groupClass.id, !groupClass.isActive).subscribe({
      next: () => {
        this.toggling.set(null);
        this.toast.success(groupClass.isActive ? 'classes.deactivatedToast' : 'classes.activatedToast');
        this.list.load();
      },
      error: (err: unknown) => {
        this.toggling.set(null);
        this.list.error.set(this.errors.message(err));
      },
    });
  }
}
