import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';

const sameAsNew = (group: AbstractControl): ValidationErrors | null => {
  const { newPassword, confirm } = group.getRawValue() as { newPassword: string; confirm: string };
  return confirm && newPassword !== confirm ? { mismatch: true } : null;
};

/**
 * Cambio de clave. Es obligatorio cuando la cuenta tiene una clave temporal (el resto de la aplicación queda cerrado hasta cambiarla)
 * y voluntario en cualquier otro momento. El servidor cierra todas las sesiones al cambiarla: se vuelve a iniciar sesión.
 */
@Component({
  selector: 'fc-change-password',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="login-page">
      <section class="login-card card">
        <div class="login-brand">
          <img class="brand-logo" src="brand/logo.png" alt="" width="381" height="328" />
          <h1>{{ 'account.changePassword' | transloco }}</h1>
          <p class="muted">{{ (forced() ? 'account.forcedHint' : 'account.changeHint') | transloco }}</p>
        </div>

        @if (error()) {
          <p class="alert alert-danger" role="alert">{{ error() }}</p>
        }

        <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
          <label class="field">
            <span>{{ (forced() ? 'account.temporaryPassword' : 'account.currentPassword') | transloco }}</span>
            <input formControlName="currentPassword" type="password" autocomplete="current-password" />
          </label>
          <label class="field">
            <span>{{ 'account.newPassword' | transloco }}</span>
            <input formControlName="newPassword" type="password" autocomplete="new-password" />
            <small class="muted">{{ 'account.passwordRules' | transloco }}</small>
          </label>
          <label class="field">
            <span>{{ 'account.confirmPassword' | transloco }}</span>
            <input formControlName="confirm" type="password" autocomplete="new-password" />
            @if (form.hasError('mismatch') && form.controls.confirm.touched) {
              <small class="field-error" role="alert">{{ 'account.mismatch' | transloco }}</small>
            }
          </label>
          <button class="btn btn-primary btn-block" type="submit" [disabled]="saving()">
            {{ (saving() ? 'common.saving' : 'account.change') | transloco }}
          </button>
        </form>

        <div class="login-lang">
          @if (forced()) {
            <button type="button" class="btn btn-ghost" (click)="cancel()">{{ 'nav.logout' | transloco }}</button>
          } @else {
            <a class="btn btn-ghost" routerLink="/">{{ 'common.back' | transloco }}</a>
          }
        </div>
      </section>
    </main>
  `,
})
export class ChangePassword {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);
  private readonly errors = inject(ErrorMessages);
  private readonly fb = inject(FormBuilder);

  protected readonly forced = computed(() => this.session.user()?.mustChangePassword ?? false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group(
    {
      currentPassword: ['', [Validators.required]],
      newPassword: ['', [Validators.required, Validators.minLength(10)]],
      confirm: ['', [Validators.required]],
    },
    { validators: [sameAsNew] },
  );

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set(this.errors.text('account.invalidForm'));
      return;
    }
    const { currentPassword, newPassword } = this.form.getRawValue();
    this.saving.set(true);
    this.error.set(null);
    this.api.changePassword(currentPassword, newPassword).subscribe({
      next: () => {
        // El servidor revocó todas las sesiones: se limpia la local y se pide iniciar sesión con la clave nueva.
        this.session.clear();
        void this.router.navigate(['/login'], { queryParams: { reason: 'passwordChanged' } });
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected cancel(): void {
    this.session.logout().subscribe(() => void this.router.navigate(['/login']));
  }
}
