import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { switchMap } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { AvailabilitySlotRequest, Member, Trainer, TrainerAssignment, TrainerAvailability } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { Confirm } from '../../shared/confirm';
import { FcDate } from '../../shared/format.pipes';
import { MemberPicker } from '../../shared/member-picker';
import { Modal } from '../../shared/modal';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';
import { MemberTrainerPanel } from './member-trainer-panel';

type Dialog = 'edit' | 'assign' | 'unassign' | 'status' | 'notes' | null;
type Day = AvailabilitySlotRequest['day'];

const DAYS: Day[] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

/** Perfil de un entrenador: datos, disponibilidad semanal, alumnos asignados (con historial) y su desactivación. */
@Component({
  selector: 'fc-trainer-detail',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe, Badge, Confirm, Modal, ReasonDialog, MemberPicker, FcDate, MemberTrainerPanel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error() && !trainer()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
      <a routerLink="/trainers" class="btn">{{ 'common.back' | transloco }}</a>
    } @else if (trainer(); as t) {
      <header class="page-header">
        <div>
          <h1>{{ t.fullName }}</h1>
          <p class="muted">
            {{ t.email }}
            <fc-badge [tone]="t.status === 'Active' ? 'ok' : 'off'">{{ 'enums.trainerStatus.' + t.status | transloco }}</fc-badge>
          </p>
        </div>
      </header>

      @if (error()) {
        <p class="alert alert-danger" role="alert">{{ error() }}</p>
      }

      @if (canManage()) {
        <div class="inline-actions">
          <button type="button" class="btn" (click)="openEdit(t)">{{ 'common.edit' | transloco }}</button>
          <button type="button" class="btn" [class.btn-danger]="t.status === 'Active'" (click)="dialog.set('status')">
            {{ (t.status === 'Active' ? 'trainers.deactivate' : 'trainers.activate') | transloco }}
          </button>
        </div>
      }

      <div class="grid-2">
        <section class="card section">
          <h2>{{ 'trainers.profile' | transloco }}</h2>
          <dl class="kv">
            <dt>{{ 'trainers.specialties' | transloco }}</dt>
            <dd>
              @for (s of t.specialties; track s) {
                <span class="badge">{{ s }}</span>
              } @empty {
                —
              }
            </dd>
            <dt>{{ 'trainers.certifications' | transloco }}</dt>
            <dd>{{ t.certifications ?? '—' }}</dd>
            <dt>{{ 'trainers.bio' | transloco }}</dt>
            <dd>{{ t.bio ?? '—' }}</dd>
            <dt>{{ 'trainers.joinedOn' | transloco }}</dt>
            <dd>{{ t.joinedOn | fcDate }}</dd>
            <dt>{{ 'common.branch' | transloco }}</dt>
            <dd>{{ branchNames(t) }}</dd>
          </dl>
        </section>

        <section class="card section">
          <h2>{{ 'trainers.availability' | transloco }}</h2>
          @if (availabilityError()) {
            <p class="alert alert-danger" role="alert">{{ availabilityError() }}</p>
          }
          <table class="table">
            <tbody>
              @for (slot of slots(); track $index) {
                <tr>
                  <td>{{ 'enums.dayOfWeek.' + slot.day | transloco }}</td>
                  <td>{{ slot.start }} – {{ slot.end }}</td>
                  <td>{{ slot.branchId ? catalog.nameOf(slot.branchId) : ('trainers.anyBranch' | transloco) }}</td>
                  <td class="actions">
                    @if (canEditAvailability()) {
                      <button type="button" class="btn btn-ghost" [attr.aria-label]="'common.delete' | transloco" (click)="removeSlot($index)">×</button>
                    }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="4" class="empty">{{ 'trainers.noAvailability' | transloco }}</td></tr>
              }
            </tbody>
          </table>
          @if (canEditAvailability()) {
            <form class="form-grid" [formGroup]="slotForm" (ngSubmit)="addSlot()" novalidate>
              <label class="field">
                <span>{{ 'trainers.day' | transloco }}</span>
                <select formControlName="day">
                  @for (d of days; track d) {
                    <option [value]="d">{{ 'enums.dayOfWeek.' + d | transloco }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span>{{ 'trainers.from' | transloco }}</span>
                <input type="time" formControlName="start" />
              </label>
              <label class="field">
                <span>{{ 'trainers.to' | transloco }}</span>
                <input type="time" formControlName="end" />
              </label>
              <label class="field">
                <span>{{ 'common.branch' | transloco }}</span>
                <select formControlName="branchId">
                  <option value="">{{ 'trainers.anyBranch' | transloco }}</option>
                  @for (id of t.branchIds; track id) {
                    <option [value]="id">{{ catalog.nameOf(id) }}</option>
                  }
                </select>
              </label>
              <div class="inline-actions">
                <button type="submit" class="btn">{{ 'trainers.addSlot' | transloco }}</button>
                <button type="button" class="btn btn-primary" [disabled]="!dirty() || savingAvailability()" (click)="saveAvailability()">
                  {{ 'trainers.saveAvailability' | transloco }}
                </button>
              </div>
            </form>
          }
        </section>
      </div>

      @if (canSeeMembers()) {
        <section class="section">
          <h2>{{ 'trainers.members' | transloco }}</h2>
          <div class="toolbar">
            <label class="check">
              <input type="checkbox" [checked]="showHistory()" (change)="toggleHistory($any($event.target).checked)" />
              <span>{{ 'trainers.showHistory' | transloco }}</span>
            </label>
            <span class="topbar-spacer"></span>
            @if (canManage() && t.status === 'Active') {
              <button type="button" class="btn btn-primary" (click)="openAssign()">{{ 'trainers.assign' | transloco }}</button>
            }
          </div>
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'attendance.member' | transloco }}</th>
                  <th>{{ 'common.branch' | transloco }}</th>
                  <th>{{ 'trainers.assignedOn' | transloco }}</th>
                  <th>{{ 'trainers.endedOn' | transloco }}</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (a of assignments(); track a.id) {
                  <tr>
                    <td>{{ a.memberName }} <small class="muted">{{ a.memberCode }}</small></td>
                    <td>{{ catalog.nameOf(a.branchId) }}</td>
                    <td>{{ a.assignedOn | fcDate }}</td>
                    <td>
                      @if (a.endedOn) {
                        {{ a.endedOn | fcDate }}<br /><small class="muted">{{ a.endReason }}</small>
                      } @else {
                        <fc-badge tone="ok">{{ 'trainers.active' | transloco }}</fc-badge>
                      }
                    </td>
                    <td class="actions">
                      @if (a.isActive && canFollow()) {
                        <a class="btn" [routerLink]="['/members', a.memberId, 'wellness']" [queryParams]="{ name: a.memberName }">{{ 'wellness.follow' | transloco }}</a>
                      }
                      @if (a.isActive && canNotes() && isSelf()) {
                        <button type="button" class="btn" (click)="openNotes(a)">{{ 'trainers.notes' | transloco }}</button>
                      }
                      @if (canManage() && a.isActive) {
                        <button type="button" class="btn" (click)="startUnassign(a)">{{ 'trainers.unassign' | transloco }}</button>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="5" class="empty">{{ 'trainers.noMembers' | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }

      @switch (dialog()) {
        @case ('edit') {
          <fc-modal [title]="'common.edit' | transloco" (closed)="dialog.set(null)">
            <form id="trainer-edit" [formGroup]="editForm" (ngSubmit)="saveProfile(t)" novalidate>
              @if (dialogError()) {
                <p class="alert alert-danger" role="alert">{{ dialogError() }}</p>
              }
              <label class="field">
                <span>{{ 'trainers.specialties' | transloco }}</span>
                <input formControlName="specialties" maxlength="300" />
                <small class="muted">{{ 'trainers.specialtiesHint' | transloco }}</small>
              </label>
              <label class="field">
                <span>{{ 'trainers.certifications' | transloco }}</span>
                <textarea formControlName="certifications" maxlength="500"></textarea>
              </label>
              <label class="field">
                <span>{{ 'trainers.bio' | transloco }}</span>
                <textarea formControlName="bio" maxlength="1500"></textarea>
              </label>
              <label class="field">
                <span>{{ 'trainers.joinedOn' | transloco }}</span>
                <input type="date" formControlName="joinedOn" />
              </label>
            </form>
            <ng-container footer>
              <button type="button" class="btn" (click)="dialog.set(null)">{{ 'common.cancel' | transloco }}</button>
              <button type="submit" form="trainer-edit" class="btn btn-primary" [disabled]="busy()">{{ 'common.save' | transloco }}</button>
            </ng-container>
          </fc-modal>
        }
        @case ('assign') {
          <fc-modal [title]="'trainers.assign' | transloco" (closed)="dialog.set(null)">
            @if (dialogError()) {
              <p class="alert alert-danger" role="alert">{{ dialogError() }}</p>
            }
            <div class="field">
              <span>{{ 'attendance.member' | transloco }}</span>
              <fc-member-picker (picked)="pickedMember.set($event)" />
              <small class="muted">{{ 'trainers.assignHint' | transloco }}</small>
            </div>
            <label class="field">
              <span>{{ 'trainers.assignReason' | transloco }}</span>
              <input maxlength="300" (input)="assignReason.set($any($event.target).value)" />
            </label>
            <ng-container footer>
              <button type="button" class="btn" (click)="dialog.set(null)">{{ 'common.cancel' | transloco }}</button>
              <button type="button" class="btn btn-primary" [disabled]="busy() || !pickedMember()" (click)="assign(t)">{{ 'trainers.assign' | transloco }}</button>
            </ng-container>
          </fc-modal>
        }
        @case ('unassign') {
          <fc-reason-dialog
            [title]="'trainers.unassign' | transloco"
            [label]="'common.reason' | transloco"
            [confirmLabel]="'trainers.unassign' | transloco"
            [danger]="true"
            [minLength]="3"
            [busy]="busy()"
            [error]="dialogError()"
            (submitted)="unassign(t, $event)"
            (cancelled)="dialog.set(null)"
          />
        }
        @case ('notes') {
          <fc-modal [title]="notesTarget()?.memberName ?? ''" (closed)="dialog.set(null)">
            @if (notesTarget(); as target) {
              <fc-member-trainer-panel [memberId]="target.memberId" />
            }
            <ng-container footer>
              <button type="button" class="btn btn-primary" (click)="dialog.set(null)">{{ 'common.close' | transloco }}</button>
            </ng-container>
          </fc-modal>
        }
        @case ('status') {
          <fc-confirm
            [title]="(t.status === 'Active' ? 'trainers.deactivate' : 'trainers.activate') | transloco"
            [message]="(t.status === 'Active' ? 'trainers.deactivateConfirm' : 'trainers.activateConfirm') | transloco"
            [confirmLabel]="(t.status === 'Active' ? 'trainers.deactivate' : 'trainers.activate') | transloco"
            [danger]="t.status === 'Active'"
            (confirmed)="toggleStatus(t)"
            (cancelled)="dialog.set(null)"
          />
        }
      }
    } @else {
      <p class="muted">{{ 'common.loading' | transloco }}</p>
    }
  `,
})
export class TrainerDetail implements OnInit {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly days = DAYS;
  protected readonly trainer = signal<Trainer | null>(null);
  protected readonly assignments = signal<TrainerAssignment[]>([]);
  protected readonly slots = signal<AvailabilitySlotRequest[]>([]);
  protected readonly dirty = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly availabilityError = signal<string | null>(null);
  protected readonly dialog = signal<Dialog>(null);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly savingAvailability = signal(false);
  protected readonly showHistory = signal(false);
  protected readonly pickedMember = signal<Member | null>(null);
  protected readonly assignReason = signal('');
  private target: TrainerAssignment | null = null;

  protected readonly canManage = computed(() => this.session.has(Perm.TrainersManage));
  protected readonly canNotes = computed(() => this.session.has(Perm.TrainersNotes));
  protected readonly canFollow = computed(() => this.session.has(Perm.TrainingMemberRead) || this.session.has(Perm.AssessmentsRead));
  protected readonly notesTarget = signal<TrainerAssignment | null>(null);
  protected readonly isSelf = computed(() => this.trainer()?.userId === this.session.user()?.userId);
  protected readonly canEditAvailability = computed(() => this.canManage() || this.isSelf());
  protected readonly canSeeMembers = computed(() => this.canManage() || this.isSelf());

  protected readonly slotForm = this.fb.nonNullable.group({
    day: ['Monday' as Day],
    start: ['06:00', [Validators.required]],
    end: ['10:00', [Validators.required]],
    branchId: [''],
  });

  protected readonly editForm = this.fb.nonNullable.group({
    specialties: [''],
    certifications: [''],
    bio: [''],
    joinedOn: ['', [Validators.required]],
  });

  ngOnInit(): void {
    this.catalog.ensure();
    this.route.paramMap
      .pipe(
        switchMap((params) => this.api.trainer(params.get('id') ?? '')),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (trainer) => {
          this.trainer.set(trainer);
          this.loadAvailability(trainer.id);
          this.loadMembers();
        },
        error: (err: unknown) => this.error.set(this.errors.message(err)),
      });
  }

  protected branchNames(trainer: Trainer): string {
    return trainer.branchIds.map((id) => this.catalog.nameOf(id)).filter((name) => name).join(', ') || '—';
  }

  protected openEdit(trainer: Trainer): void {
    this.dialogError.set(null);
    this.editForm.reset({
      specialties: trainer.specialties.join(', '),
      certifications: trainer.certifications ?? '',
      bio: trainer.bio ?? '',
      joinedOn: trainer.joinedOn,
    });
    this.dialog.set('edit');
  }

  protected saveProfile(trainer: Trainer): void {
    if (this.editForm.invalid) {
      this.editForm.markAllAsTouched();
      return;
    }
    const v = this.editForm.getRawValue();
    this.run(
      this.api.updateTrainer(trainer.id, {
        specialties: v.specialties.split(',').map((s) => s.trim()).filter((s) => s.length > 0),
        certifications: v.certifications.trim() || null,
        bio: v.bio.trim() || null,
        joinedOn: v.joinedOn,
      }),
      'trainers.savedToast',
      (updated) => this.trainer.set(updated),
    );
  }

  protected toggleStatus(trainer: Trainer): void {
    this.run(this.api.setTrainerStatus(trainer.id, trainer.status !== 'Active'), 'trainers.savedToast', (updated) => this.trainer.set(updated));
  }

  protected openAssign(): void {
    this.dialogError.set(null);
    this.pickedMember.set(null);
    this.assignReason.set('');
    this.dialog.set('assign');
  }

  protected assign(trainer: Trainer): void {
    const member = this.pickedMember();
    if (!member) {
      return;
    }
    this.run(this.api.assignMember(trainer.id, member.id, this.assignReason().trim() || null), 'trainers.assignedToast', () => this.afterAssignmentChange(trainer.id));
  }

  protected openNotes(assignment: TrainerAssignment): void {
    this.notesTarget.set(assignment);
    this.dialog.set('notes');
  }

  protected startUnassign(assignment: TrainerAssignment): void {
    this.target = assignment;
    this.dialogError.set(null);
    this.dialog.set('unassign');
  }

  protected unassign(trainer: Trainer, reason: string): void {
    const assignment = this.target;
    if (!assignment) {
      return;
    }
    this.run(this.api.unassignMember(trainer.id, assignment.memberId, reason), 'trainers.unassignedToast', () => this.afterAssignmentChange(trainer.id));
  }

  protected toggleHistory(show: boolean): void {
    this.showHistory.set(show);
    this.loadMembers();
  }

  protected addSlot(): void {
    if (this.slotForm.invalid) {
      return;
    }
    const v = this.slotForm.getRawValue();
    this.slots.update((list) => [...list, { day: v.day, start: v.start, end: v.end, branchId: v.branchId || null }]);
    this.dirty.set(true);
  }

  protected removeSlot(index: number): void {
    this.slots.update((list) => list.filter((_, i) => i !== index));
    this.dirty.set(true);
  }

  protected saveAvailability(): void {
    const trainer = this.trainer();
    if (!trainer) {
      return;
    }
    this.savingAvailability.set(true);
    this.availabilityError.set(null);
    this.api.setTrainerAvailability(trainer.id, this.slots()).subscribe({
      next: (saved) => {
        this.savingAvailability.set(false);
        this.slots.set(saved.map(toRequest));
        this.dirty.set(false);
        this.toast.success('trainers.availabilitySavedToast');
      },
      error: (err: unknown) => {
        this.savingAvailability.set(false);
        this.availabilityError.set(this.errors.message(err));
      },
    });
  }

  /** El aviso de éxito y el cierre del diálogo solo ocurren cuando el servidor confirmó; un error se muestra en el propio diálogo. */
  private run<T>(request: import('rxjs').Observable<T>, toastKey: string, onDone: (result: T) => void): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.dialogError.set(null);
    request.subscribe({
      next: (result) => {
        this.busy.set(false);
        this.dialog.set(null);
        this.toast.success(toastKey);
        onDone(result);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.dialogError.set(this.errors.message(err));
      },
    });
  }

  private afterAssignmentChange(trainerId: string): void {
    this.loadMembers();
    this.api.trainer(trainerId).subscribe((updated) => this.trainer.set(updated));
  }

  private loadAvailability(id: string): void {
    this.api.trainerAvailability(id).subscribe({
      next: (list) => {
        this.slots.set(list.map(toRequest));
        this.dirty.set(false);
      },
      error: (err: unknown) => this.availabilityError.set(this.errors.message(err)),
    });
  }

  private loadMembers(): void {
    const trainer = this.trainer();
    if (!trainer || !this.canSeeMembers()) {
      return;
    }
    this.api.trainerMembers(trainer.id, { activeOnly: !this.showHistory(), pageSize: 100 }).subscribe({
      next: (page) => this.assignments.set(page.items),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }
}

const toRequest = (slot: TrainerAvailability): AvailabilitySlotRequest => ({
  day: slot.day as Day,
  start: slot.start,
  end: slot.end,
  branchId: slot.branchId,
});
