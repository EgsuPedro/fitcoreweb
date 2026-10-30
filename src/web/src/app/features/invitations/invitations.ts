import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Branch, Invitation, Role } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { Confirm } from '../../shared/confirm';
import { Modal } from '../../shared/modal';
import { ToastService } from '../../shared/toast';

/**
 * Invitaciones de personal. Quien invita elige el correo, el rol y las sucursales; la persona recibe un enlace de un solo uso (vence en 7 días) para crear
 * su contraseña. Las invitaciones pendientes reservan plaza en el plan. El servidor aplica las mismas reglas que al crear un usuario a mano.
 */
@Component({
  selector: 'fc-invitations',
  imports: [ReactiveFormsModule, TranslocoPipe, DatePipe, Badge, Confirm, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'invitations.title' | transloco }}</h1>
        <p class="muted">{{ 'invitations.subtitle' | transloco }}</p>
      </div>
      @if (canInvite()) {
        <button type="button" class="btn btn-primary" (click)="openInvite()">{{ 'invitations.new' | transloco }}</button>
      }
    </header>

    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    <div class="card table-card">
      <table class="table">
        <caption class="sr-only">{{ 'invitations.title' | transloco }}</caption>
        <thead>
          <tr>
            <th scope="col">{{ 'invitations.email' | transloco }}</th>
            <th scope="col">{{ 'invitations.role' | transloco }}</th>
            <th scope="col">{{ 'common.status' | transloco }}</th>
            <th scope="col">{{ 'invitations.expires' | transloco }}</th>
            <th scope="col"></th>
          </tr>
        </thead>
        <tbody>
          @for (item of items(); track item.id) {
            <tr>
              <th scope="row">{{ item.email }}</th>
              <td>{{ item.roleName }}</td>
              <td><fc-badge [tone]="item.status === 'Accepted' ? 'ok' : item.status === 'Pending' ? 'warn' : 'off'">{{ 'invitations.status.' + item.status | transloco }}</fc-badge></td>
              <td>{{ item.expiresAt | date: 'short' }}</td>
              <td class="row-actions">
                @if (canInvite() && (item.status === 'Pending' || item.status === 'Expired')) {
                  @if (item.status === 'Pending') {
                    <button type="button" class="btn" [disabled]="busy()" (click)="resend(item)">{{ 'invitations.resend' | transloco }}</button>
                    <button type="button" class="btn btn-danger" [disabled]="busy()" (click)="revoking.set(item)">{{ 'invitations.revoke' | transloco }}</button>
                  }
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="5" class="empty">{{ (loading() ? 'common.loading' : 'invitations.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>

    @if (inviting()) {
      <fc-modal [title]="'invitations.new' | transloco" (closed)="inviting.set(false)">
        <form id="invite-form" [formGroup]="form" (ngSubmit)="send()" novalidate>
          @if (formError()) { <p class="alert alert-danger" role="alert">{{ formError() }}</p> }
          <label class="field">
            <span>{{ 'invitations.email' | transloco }}</span>
            <input formControlName="email" type="email" maxlength="256" autocomplete="off" />
          </label>
          <label class="field">
            <span>{{ 'invitations.role' | transloco }}</span>
            <select formControlName="roleId">
              <option value="">—</option>
              @for (role of roles(); track role.id) { <option [value]="role.id">{{ role.name }}</option> }
            </select>
          </label>
          <fieldset class="field">
            <legend>{{ 'users.branches' | transloco }}</legend>
            @for (branch of branches(); track branch.id) {
              <label class="check">
                <input type="checkbox" [checked]="selectedBranches().has(branch.id)" (change)="toggleBranch(branch.id, $any($event.target).checked)" />
                <span>{{ branch.code }} — {{ branch.name }}</span>
              </label>
            } @empty {
              <p class="muted">{{ 'users.noBranches' | transloco }}</p>
            }
          </fieldset>
          <p class="muted">{{ 'invitations.hint' | transloco }}</p>
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="inviting.set(false)">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="invite-form" class="btn btn-primary" [disabled]="saving()">{{ 'invitations.send' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (revoking(); as target) {
      <fc-confirm
        [title]="'invitations.revoke' | transloco"
        [message]="'invitations.revokeConfirm' | transloco: { email: target.email }"
        [confirmLabel]="'invitations.revoke' | transloco"
        [danger]="true"
        (confirmed)="revoke(target)"
        (cancelled)="revoking.set(null)"
      />
    }
  `,
})
export class Invitations implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  protected readonly items = signal<Invitation[]>([]);
  protected readonly roles = signal<Role[]>([]);
  protected readonly branches = signal<Branch[]>([]);
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly saving = signal(false);
  protected readonly inviting = signal(false);
  protected readonly revoking = signal<Invitation | null>(null);
  protected readonly selectedBranches = signal<ReadonlySet<string>>(new Set());
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);

  protected readonly canInvite = computed(() => this.session.has(Perm.UsersCreate));
  // Se ofrecen todos los roles del gimnasio: el servidor decide cuáles puede dar quien invita (algunos permisos sensibles se pueden conceder sin poseerlos).

  protected readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    roleId: ['', [Validators.required]],
  });

  ngOnInit(): void {
    this.load();
    this.api.branches({ pageSize: 100 }).subscribe({ next: (r) => this.branches.set(r.items), error: () => undefined });
    this.api.roles().subscribe({ next: (r) => this.roles.set(r), error: () => undefined });
  }

  protected openInvite(): void {
    this.form.reset({ email: '', roleId: '' });
    this.selectedBranches.set(new Set());
    this.formError.set(null);
    this.inviting.set(true);
  }

  protected toggleBranch(id: string, checked: boolean): void {
    this.selectedBranches.update((current) => {
      const next = new Set(current);
      if (checked) { next.add(id); } else { next.delete(id); }
      return next;
    });
  }

  protected send(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.errors.text('invitations.invalidForm'));
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.formError.set(null);
    this.api.createInvitation({ email: v.email, roleId: v.roleId, branchIds: [...this.selectedBranches()] }).subscribe({
      next: () => {
        this.saving.set(false);
        this.inviting.set(false);
        this.toast.success('invitations.sent');
        this.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  protected resend(item: Invitation): void {
    this.run(this.api.resendInvitation(item.id), 'invitations.resent');
  }

  protected revoke(item: Invitation): void {
    this.revoking.set(null);
    this.run(this.api.revokeInvitation(item.id), 'invitations.revoked');
  }

  private run(call: ReturnType<ApiService['resendInvitation']>, toastKey: string): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success(toastKey);
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  private load(): void {
    this.api.invitations().subscribe({
      next: (list) => {
        this.items.set(list);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
