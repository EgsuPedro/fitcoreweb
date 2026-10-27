import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Permission, Role } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Confirm } from '../../shared/confirm';
import { Modal } from '../../shared/modal';

const OWNER_ROLE = 'Propietario';

interface PermissionGroup {
  module: string;
  permissions: Permission[];
}

@Component({
  selector: 'fc-roles',
  imports: [ReactiveFormsModule, TranslocoPipe, Modal, Confirm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'roles.title' | transloco }}</h1>
      @if (canManage()) {
        <button type="button" class="btn btn-primary" (click)="openCreate()">{{ 'roles.new' | transloco }}</button>
      }
    </header>

    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'roles.name' | transloco }}</th>
            <th>{{ 'roles.description' | transloco }}</th>
            <th>{{ 'roles.scope' | transloco }}</th>
            <th>{{ 'roles.permissionCount' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (role of roles(); track role.id) {
            <tr>
              <td>
                <strong>{{ role.name }}</strong>
                @if (role.isSystem) {
                  <span class="badge">{{ 'roles.system' | transloco }}</span>
                }
              </td>
              <td>{{ role.description ?? '—' }}</td>
              <td>{{ (role.grantsAllBranches ? 'roles.allBranches' : 'roles.assignedBranches') | transloco }}</td>
              <td>{{ role.permissions.length }}</td>
              <td class="actions">
                <button type="button" class="btn" (click)="openEdit(role)">
                  {{ (canEdit(role) ? 'common.edit' : 'common.view') | transloco }}
                </button>
                @if (canDelete(role)) {
                  <button type="button" class="btn btn-danger" (click)="deleteTarget.set(role)">{{ 'common.delete' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr>
              <td colspan="5" class="empty">{{ (loading() ? 'common.loading' : 'roles.empty') | transloco }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>

    @if (editor(); as target) {
      <fc-modal [title]="(target === 'new' ? 'roles.new' : readOnly() ? 'roles.view' : 'roles.edit') | transloco" (closed)="closeEditor()">
        <form id="role-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
          @if (formError()) {
            <p class="alert alert-danger" role="alert">{{ formError() }}</p>
          }
          @if (readOnly()) {
            <p class="alert alert-warning">{{ (isOwner(target) ? 'roles.ownerLocked' : 'roles.readOnly') | transloco }}</p>
          }

          <label class="field">
            <span>{{ 'roles.name' | transloco }}</span>
            <input formControlName="name" maxlength="60" />
          </label>
          <label class="field">
            <span>{{ 'roles.description' | transloco }}</span>
            <input formControlName="description" maxlength="300" />
          </label>
          <label class="check" [class.disabled]="!session.allBranches()">
            <input type="checkbox" formControlName="grantsAllBranches" />
            <span>{{ 'roles.allBranches' | transloco }}</span>
          </label>

          @for (group of groups(); track group.module) {
            <fieldset class="field">
              <legend>{{ 'modules.' + group.module | transloco }}</legend>
              @for (permission of group.permissions; track permission.code) {
                <label class="check" [class.disabled]="!grantable(permission.code)">
                  <input
                    type="checkbox"
                    [checked]="selected().has(permission.code)"
                    [disabled]="readOnly() || !grantable(permission.code)"
                    (change)="toggle(permission.code, $any($event.target).checked)"
                  />
                  <span>{{ permission.description }}</span>
                  <code class="muted">{{ permission.code }}</code>
                </label>
              }
            </fieldset>
          }
          @if (!readOnly()) {
            <p class="muted">{{ 'roles.grantHint' | transloco }}</p>
          }
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="closeEditor()">{{ (readOnly() ? 'common.close' : 'common.cancel') | transloco }}</button>
          @if (!readOnly()) {
            <button type="submit" form="role-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
          }
        </ng-container>
      </fc-modal>
    }

    @if (deleteTarget(); as role) {
      <fc-confirm
        [title]="'roles.delete' | transloco"
        [message]="'roles.deleteConfirm' | transloco: { name: role.name }"
        [confirmLabel]="'common.delete' | transloco"
        [danger]="true"
        (confirmed)="remove(role)"
        (cancelled)="deleteTarget.set(null)"
      />
    }
  `,
})
export class Roles implements OnInit {
  private readonly api = inject(ApiService);
  protected readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly transloco = inject(TranslocoService);
  private readonly fb = inject(FormBuilder);

  protected readonly canManage = computed(() => this.session.has(Perm.RolesManage));

  protected readonly roles = signal<Role[]>([]);
  protected readonly catalog = signal<Permission[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly editor = signal<Role | 'new' | null>(null);
  protected readonly selected = signal<ReadonlySet<string>>(new Set());
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly deleteTarget = signal<Role | null>(null);

  protected readonly groups = computed<PermissionGroup[]>(() => {
    const byModule = new Map<string, Permission[]>();
    for (const permission of this.catalog()) {
      byModule.set(permission.module, [...(byModule.get(permission.module) ?? []), permission]);
    }
    return [...byModule].map(([module, permissions]) => ({ module, permissions }));
  });

  protected readonly readOnly = computed(() => {
    const target = this.editor();
    return target !== null && target !== 'new' && !this.canEdit(target);
  });

  protected readonly form = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.minLength(2), Validators.maxLength(60)]],
    description: ['', [Validators.maxLength(300)]],
    grantsAllBranches: [false],
  });

  ngOnInit(): void {
    this.load();
    this.api.permissions().subscribe({ next: (p) => this.catalog.set(p), error: (err: unknown) => this.error.set(this.errors.message(err)) });
  }

  protected isOwner(role: Role | 'new'): boolean {
    return role !== 'new' && role.isSystem && role.name === OWNER_ROLE;
  }

  /** Editable: hay permiso de gestión, no es Propietario, y el usuario posee todo lo que el rol otorga. */
  protected canEdit(role: Role): boolean {
    return (
      this.canManage() &&
      !this.isOwner(role) &&
      role.permissions.every((p) => this.session.has(p)) &&
      (!role.grantsAllBranches || this.session.allBranches())
    );
  }

  protected canDelete(role: Role): boolean {
    return this.canEdit(role) && !role.isSystem;
  }

  /** Solo se pueden conceder permisos que uno mismo posee (la API aplica la misma regla). */
  protected grantable(code: string): boolean {
    return this.session.has(code);
  }

  protected openCreate(): void {
    this.form.reset({ name: '', description: '', grantsAllBranches: false });
    this.form.enable();
    this.applyBranchRule();
    this.selected.set(new Set());
    this.formError.set(null);
    this.editor.set('new');
  }

  protected openEdit(role: Role): void {
    this.form.reset({ name: role.name, description: role.description ?? '', grantsAllBranches: role.grantsAllBranches });
    this.form.enable();
    if (!this.canEdit(role)) {
      this.form.disable();
    } else {
      if (role.isSystem) {
        this.form.controls.name.disable();
      }
      this.applyBranchRule();
    }
    this.selected.set(new Set(role.permissions));
    this.formError.set(null);
    this.editor.set(role);
  }

  protected closeEditor(): void {
    this.editor.set(null);
  }

  protected toggle(code: string, checked: boolean): void {
    this.selected.update((current) => {
      const next = new Set(current);
      if (checked) {
        next.add(code);
      } else {
        next.delete(code);
      }
      return next;
    });
  }

  protected save(): void {
    const target = this.editor();
    if (!target || this.readOnly()) {
      return;
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.transloco.translate('roles.invalidForm'));
      return;
    }

    const value = this.form.getRawValue();
    const body = {
      name: value.name.trim(),
      description: value.description.trim() || null,
      grantsAllBranches: value.grantsAllBranches,
      permissions: [...this.selected()],
    };

    this.saving.set(true);
    this.formError.set(null);
    const request = target === 'new' ? this.api.createRole(body) : this.api.updateRole(target.id, body);
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

  protected remove(role: Role): void {
    this.deleteTarget.set(null);
    this.api.deleteRole(role.id).subscribe({
      next: () => this.load(),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  /** Quien no accede a todas las sucursales no puede crear roles que las abarquen. */
  private applyBranchRule(): void {
    if (!this.session.allBranches()) {
      this.form.controls.grantsAllBranches.disable();
    }
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.roles().subscribe({
      next: (roles) => {
        this.roles.set(roles);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
