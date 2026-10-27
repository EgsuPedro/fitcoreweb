import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Trainer, User } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

/** Entrenadores del gimnasio: listado y habilitación de un usuario como entrenador. */
@Component({
  selector: 'fc-trainers',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe, Badge, Modal, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'trainers.title' | transloco }}</h1>
      @if (canManage()) {
        <button type="button" class="btn btn-primary" (click)="openCreate()">{{ 'trainers.new' | transloco }}</button>
      }
    </header>

    <div class="toolbar">
      <input type="search" class="search" [placeholder]="'common.search' | transloco" [attr.aria-label]="'common.search' | transloco" (input)="list.search($any($event.target).value)" />
      <select [attr.aria-label]="'common.status' | transloco" (change)="list.set({ status: $any($event.target).value })">
        <option value="">{{ 'common.all' | transloco }}</option>
        <option value="Active">{{ 'enums.trainerStatus.Active' | transloco }}</option>
        <option value="Inactive">{{ 'enums.trainerStatus.Inactive' | transloco }}</option>
      </select>
      <select [attr.aria-label]="'common.branch' | transloco" (change)="list.set({ branchId: $any($event.target).value })">
        <option value="">{{ 'common.allBranches' | transloco }}</option>
        @for (branch of catalog.branches(); track branch.id) {
          <option [value]="branch.id">{{ branch.name }}</option>
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
            <th>{{ 'trainers.name' | transloco }}</th>
            <th>{{ 'trainers.specialties' | transloco }}</th>
            <th>{{ 'common.branch' | transloco }}</th>
            <th class="num">{{ 'trainers.activeMembers' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (t of list.items(); track t.id) {
            <tr>
              <td><a [routerLink]="['/trainers', t.id]">{{ t.fullName }}</a><br /><small class="muted">{{ t.email }}</small></td>
              <td>
                @for (s of t.specialties; track s) {
                  <span class="badge">{{ s }}</span>
                } @empty {
                  <span class="muted">—</span>
                }
              </td>
              <td>{{ branchNames(t) }}</td>
              <td class="num">{{ t.activeMembers }}</td>
              <td><fc-badge [tone]="t.status === 'Active' ? 'ok' : 'off'">{{ 'enums.trainerStatus.' + t.status | transloco }}</fc-badge></td>
              <td class="actions"><a class="btn" [routerLink]="['/trainers', t.id]">{{ 'common.view' | transloco }}</a></td>
            </tr>
          } @empty {
            <tr><td colspan="6" class="empty">{{ (list.loading() ? 'common.loading' : 'trainers.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @if (creating()) {
      <fc-modal [title]="'trainers.new' | transloco" (closed)="creating.set(false)">
        <form id="trainer-form" [formGroup]="form" (ngSubmit)="create()" novalidate>
          @if (formError()) {
            <p class="alert alert-danger" role="alert">{{ formError() }}</p>
          }
          <label class="field">
            <span>{{ 'trainers.user' | transloco }}</span>
            <select formControlName="userId">
              <option value="">—</option>
              @for (u of candidates(); track u.id) {
                <option [value]="u.id">{{ u.fullName }} · {{ u.email }}</option>
              }
            </select>
            <small class="muted">{{ 'trainers.userHint' | transloco }}</small>
          </label>
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
          <button type="button" class="btn" (click)="creating.set(false)">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="trainer-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }
  `,
})
export class Trainers implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly list = new ListState<Trainer>((query) => this.api.trainers(query), this.errors, inject(DestroyRef));
  protected readonly canManage = computed(() => this.session.has(Perm.TrainersManage));
  protected readonly creating = signal(false);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly users = signal<User[]>([]);
  protected readonly candidates = computed(() => {
    const taken = new Set(this.list.items().map((t) => t.userId));
    return this.users().filter((u) => u.isActive && !taken.has(u.id));
  });

  protected readonly form = this.fb.nonNullable.group({
    userId: ['', [Validators.required]],
    specialties: [''],
    certifications: [''],
    bio: [''],
    joinedOn: [''],
  });

  ngOnInit(): void {
    this.catalog.ensure();
    this.list.load();
  }

  protected branchNames(trainer: Trainer): string {
    return trainer.branchIds.map((id) => this.catalog.nameOf(id)).filter((name) => name).join(', ') || '—';
  }

  protected openCreate(): void {
    this.formError.set(null);
    this.form.reset();
    // Usuarios activos del gimnasio entre los que elegir; el servidor valida alcance y duplicados.
    this.api.users({ pageSize: 100, isActive: true }).subscribe({
      next: (page) => this.users.set(page.items),
      error: (err: unknown) => this.formError.set(this.errors.message(err)),
    });
    this.creating.set(true);
  }

  protected create(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.errors.text('trainers.invalidForm'));
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .createTrainer({
        userId: v.userId,
        specialties: v.specialties.split(',').map((s) => s.trim()).filter((s) => s.length > 0),
        certifications: v.certifications.trim() || null,
        bio: v.bio.trim() || null,
        joinedOn: v.joinedOn || null,
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.creating.set(false);
          this.toast.success('trainers.createdToast');
          this.list.load();
        },
        error: (err: unknown) => {
          this.saving.set(false);
          this.formError.set(this.errors.message(err));
        },
      });
  }
}
