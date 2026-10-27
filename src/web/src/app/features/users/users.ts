import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Subject, debounceTime } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { Branch, Role, User } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { canAssignRole } from '../../core/auth/role-rules';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Confirm } from '../../shared/confirm';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';

type Editor = { mode: 'create' } | { mode: 'access'; user: User };

@Component({
  selector: 'fc-users',
  imports: [ReactiveFormsModule, TranslocoPipe, Modal, Pager, Confirm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'users.title' | transloco }}</h1>
      @if (canCreate()) {
        <button type="button" class="btn btn-primary" (click)="openCreate()">{{ 'users.new' | transloco }}</button>
      }
    </header>

    <div class="toolbar">
      <input
        type="search"
        class="search"
        [placeholder]="'common.search' | transloco"
        [attr.aria-label]="'common.search' | transloco"
        (input)="searchInput.next($any($event.target).value)"
      />
      <select [attr.aria-label]="'users.branch' | transloco" (change)="onBranchFilter($any($event.target).value)">
        <option value="">{{ 'users.allBranches' | transloco }}</option>
        @for (branch of branches(); track branch.id) {
          <option [value]="branch.id">{{ branch.code }} — {{ branch.name }}</option>
        }
      </select>
      <select [attr.aria-label]="'common.status' | transloco" (change)="onActiveFilter($any($event.target).value)">
        <option value="">{{ 'common.all' | transloco }}</option>
        <option value="true">{{ 'common.active' | transloco }}</option>
        <option value="false">{{ 'common.inactive' | transloco }}</option>
      </select>
    </div>

    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'users.name' | transloco }}</th>
            <th>{{ 'users.email' | transloco }}</th>
            <th>{{ 'users.roles' | transloco }}</th>
            <th>{{ 'users.branches' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (user of items(); track user.id) {
            <tr>
              <td><strong>{{ user.fullName }}</strong></td>
              <td>{{ user.email }}</td>
              <td>
                @for (role of user.roles; track role.id) {
                  <span class="badge">{{ role.name }}</span>
                } @empty {
                  <span class="muted">—</span>
                }
              </td>
              <td>{{ user.branchIds.length }}</td>
              <td>
                <span class="badge" [class.badge-ok]="user.isActive" [class.badge-off]="!user.isActive">
                  {{ (user.isActive ? 'common.active' : 'common.inactive') | transloco }}
                </span>
              </td>
              <td class="actions">
                @if (canUpdate() && user.id !== session.user()?.userId) {
                  <button type="button" class="btn" (click)="openAccess(user)">{{ 'users.access' | transloco }}</button>
                  <button type="button" class="btn" (click)="statusTarget.set(user)">
                    {{ (user.isActive ? 'users.deactivate' : 'users.activate') | transloco }}
                  </button>
                }
              </td>
            </tr>
          } @empty {
            <tr>
              <td colspan="6" class="empty">{{ (loading() ? 'common.loading' : 'users.empty') | transloco }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>

    <fc-pager [page]="page()" [pageSize]="pageSize" [total]="total()" (pageChange)="goTo($event)" />

    @if (editor(); as current) {
      <fc-modal [title]="(current.mode === 'create' ? 'users.new' : 'users.access') | transloco" (closed)="closeEditor()">
        <form id="user-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
          @if (formError()) {
            <p class="alert alert-danger" role="alert">{{ formError() }}</p>
          }

          @if (current.mode === 'create') {
            <label class="field">
              <span>{{ 'users.name' | transloco }}</span>
              <input formControlName="fullName" maxlength="200" autocomplete="off" />
            </label>
            <label class="field">
              <span>{{ 'users.email' | transloco }}</span>
              <input formControlName="email" type="email" maxlength="256" autocomplete="off" />
            </label>
            <label class="field">
              <span>{{ 'users.password' | transloco }}</span>
              <input formControlName="password" type="password" autocomplete="new-password" />
              <small class="muted">{{ 'users.passwordHint' | transloco }}</small>
            </label>
          } @else {
            <p class="muted">{{ current.user.fullName }} · {{ current.user.email }}</p>
          }

          <fieldset class="field">
            <legend>{{ 'users.roles' | transloco }}</legend>
            @for (role of roles(); track role.id) {
              <label class="check" [class.disabled]="!assignable(role)">
                <input
                  type="checkbox"
                  [checked]="selectedRoles().has(role.id)"
                  [disabled]="!assignable(role)"
                  (change)="toggleRole(role.id, $any($event.target).checked)"
                />
                <span>{{ role.name }}</span>
                @if (!assignable(role)) {
                  <small class="muted">{{ 'users.roleNotAssignable' | transloco }}</small>
                }
              </label>
            } @empty {
              <p class="muted">{{ 'users.noRoles' | transloco }}</p>
            }
          </fieldset>

          <fieldset class="field">
            <legend>{{ 'users.branches' | transloco }}</legend>
            @for (branch of branches(); track branch.id) {
              <label class="check">
                <input
                  type="checkbox"
                  [checked]="selectedBranches().has(branch.id)"
                  (change)="toggleBranch(branch.id, $any($event.target).checked)"
                />
                <span>{{ branch.code }} — {{ branch.name }}</span>
              </label>
            } @empty {
              <p class="muted">{{ 'users.noBranches' | transloco }}</p>
            }
          </fieldset>
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="closeEditor()">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="user-form" class="btn btn-primary" [disabled]="saving()">
            {{ 'common.save' | transloco }}
          </button>
        </ng-container>
      </fc-modal>
    }

    @if (statusTarget(); as target) {
      <fc-confirm
        [title]="(target.isActive ? 'users.deactivate' : 'users.activate') | transloco"
        [message]="(target.isActive ? 'users.deactivateConfirm' : 'users.activateConfirm') | transloco: { name: target.fullName }"
        [confirmLabel]="(target.isActive ? 'users.deactivate' : 'users.activate') | transloco"
        [danger]="target.isActive"
        (confirmed)="changeStatus(target)"
        (cancelled)="statusTarget.set(null)"
      />
    }
  `,
})
export class Users implements OnInit {
  private readonly api = inject(ApiService);
  protected readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly transloco = inject(TranslocoService);
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly pageSize = 20;
  protected readonly searchInput = new Subject<string>();

  protected readonly canCreate = computed(() => this.session.has(Perm.UsersCreate));
  protected readonly canUpdate = computed(() => this.session.has(Perm.UsersUpdate));

  protected readonly items = signal<User[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly roles = signal<Role[]>([]);
  protected readonly branches = signal<Branch[]>([]);

  protected readonly editor = signal<Editor | null>(null);
  protected readonly selectedRoles = signal<ReadonlySet<string>>(new Set());
  protected readonly selectedBranches = signal<ReadonlySet<string>>(new Set());
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly statusTarget = signal<User | null>(null);

  private search = '';
  private branchId = '';
  private isActive = '';

  protected readonly form = this.fb.nonNullable.group({
    fullName: ['', [Validators.required, Validators.maxLength(200)]],
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    password: ['', [Validators.required]],
  });

  constructor() {
    this.searchInput.pipe(debounceTime(300), takeUntilDestroyed(this.destroyRef)).subscribe((text) => {
      this.search = text;
      this.goTo(1);
    });
  }

  ngOnInit(): void {
    this.load();
    // Catálogos para filtros y asignación. Un fallo aquí (p. ej. sin permiso roles.read) no impide ver la lista.
    this.api.branches({ pageSize: 100 }).subscribe({ next: (r) => this.branches.set(r.items), error: () => undefined });
    this.api.roles().subscribe({ next: (r) => this.roles.set(r), error: () => undefined });
  }

  protected assignable(role: Role): boolean {
    return canAssignRole(role, (permission) => this.session.has(permission), this.session.allBranches());
  }

  protected onBranchFilter(value: string): void {
    this.branchId = value;
    this.goTo(1);
  }

  protected onActiveFilter(value: string): void {
    this.isActive = value;
    this.goTo(1);
  }

  protected goTo(page: number): void {
    this.page.set(page);
    this.load();
  }

  protected openCreate(): void {
    this.form.reset({ fullName: '', email: '', password: '' });
    this.form.controls.fullName.enable();
    this.form.controls.email.enable();
    this.form.controls.password.enable();
    this.selectedRoles.set(new Set());
    this.selectedBranches.set(new Set());
    this.formError.set(null);
    this.editor.set({ mode: 'create' });
  }

  protected openAccess(user: User): void {
    // En modo acceso solo se editan roles y sucursales: los campos de la cuenta no aplican.
    this.form.controls.fullName.disable();
    this.form.controls.email.disable();
    this.form.controls.password.disable();
    this.selectedRoles.set(new Set(user.roles.map((r) => r.id)));
    this.selectedBranches.set(new Set(user.branchIds));
    this.formError.set(null);
    this.editor.set({ mode: 'access', user });
  }

  protected closeEditor(): void {
    this.editor.set(null);
  }

  protected toggleRole(id: string, checked: boolean): void {
    this.selectedRoles.update((current) => toggle(current, id, checked));
  }

  protected toggleBranch(id: string, checked: boolean): void {
    this.selectedBranches.update((current) => toggle(current, id, checked));
  }

  protected save(): void {
    const current = this.editor();
    if (!current) {
      return;
    }
    if (current.mode === 'create' && this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.transloco.translate('users.invalidForm'));
      return;
    }

    const roleIds = [...this.selectedRoles()];
    const branchIds = [...this.selectedBranches()];
    const value = this.form.getRawValue();

    this.saving.set(true);
    this.formError.set(null);
    const request =
      current.mode === 'create'
        ? this.api.createUser({
            email: value.email.trim(),
            fullName: value.fullName.trim(),
            password: value.password,
            roleIds,
            branchIds,
          })
        : this.api.setUserAccess(current.user.id, { roleIds, branchIds });

    request.subscribe({
      next: () => {
        this.saving.set(false);
        this.closeEditor();
        this.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  protected changeStatus(user: User): void {
    this.statusTarget.set(null);
    this.api.setUserStatus(user.id, !user.isActive).subscribe({
      next: () => this.load(),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .users({ page: this.page(), pageSize: this.pageSize, search: this.search, branchId: this.branchId, isActive: this.isActive })
      .subscribe({
        next: (result) => {
          this.items.set(result.items);
          this.total.set(result.totalCount);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          this.loading.set(false);
          this.error.set(this.errors.message(err));
        },
      });
  }
}

function toggle(current: ReadonlySet<string>, id: string, checked: boolean): ReadonlySet<string> {
  const next = new Set(current);
  if (checked) {
    next.add(id);
  } else {
    next.delete(id);
  }
  return next;
}
