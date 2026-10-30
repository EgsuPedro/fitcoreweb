import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ErrorMessages } from '../../core/i18n/messages';
import { Lang, SUPPORTED_LANGS, setLang } from '../../core/i18n/transloco';
import { SessionService } from '../../core/auth/session.service';
import { Seo } from '../../core/seo.service';

const TENANT_KEY = 'fc_tenant';

function rememberedTenant(): string {
  try {
    return localStorage.getItem(TENANT_KEY) ?? '';
  } catch {
    return '';
  }
}

function rememberTenant(slug: string): void {
  try {
    localStorage.setItem(TENANT_KEY, slug);
  } catch {
    // Solo es una comodidad: si no se puede guardar, se escribe de nuevo la próxima vez.
  }
}

@Component({
  selector: 'fc-login',
  imports: [ReactiveFormsModule, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="login-page">
      <section class="login-card card">
        <div class="login-brand">
          <img class="brand-logo" src="brand/logo.png" alt="" width="381" height="328" />
          <h1 class="sr-only">FIT360</h1>
          <p class="muted">{{ 'login.subtitle' | transloco }}</p>
        </div>

        @if (reason() === 'passwordChanged') {
          <p class="alert alert-info" role="status">{{ 'login.passwordChanged' | transloco }}</p>
        }
        @if (reason() === 'expired') {
          <p class="alert alert-warning" role="status">{{ 'login.expired' | transloco }}</p>
        }
        @if (error()) {
          <p class="alert alert-danger" role="alert">{{ error() }}</p>
        }

        <form [formGroup]="form" (ngSubmit)="submit()" novalidate>
          <label class="field">
            <span>{{ 'login.tenant' | transloco }}</span>
            <input formControlName="tenantSlug" autocomplete="organization" autocapitalize="none" spellcheck="false" />
            <small class="muted">{{ 'login.tenantHint' | transloco }}</small>
          </label>

          <label class="field">
            <span>{{ 'login.email' | transloco }}</span>
            <input formControlName="email" type="email" autocomplete="username" />
          </label>

          <label class="field">
            <span>{{ 'login.password' | transloco }}</span>
            <input formControlName="password" type="password" autocomplete="current-password" />
          </label>

          <button class="btn btn-primary btn-block" type="submit" [disabled]="loading()">
            {{ (loading() ? 'login.submitting' : 'login.submit') | transloco }}
          </button>
        </form>

        <div class="login-lang">
          @for (lang of langs; track lang) {
            <button type="button" class="btn btn-ghost" [class.active]="activeLang() === lang" (click)="changeLang(lang)">
              {{ lang.toUpperCase() }}
            </button>
          }
        </div>
      </section>
    </main>
  `,
})
export class Login {
  private readonly seo = inject(Seo);

  constructor() {
    this.seo.noIndex();
  }
  private readonly fb = inject(FormBuilder);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly transloco = inject(TranslocoService);
  private readonly errors = inject(ErrorMessages);

  protected readonly langs = SUPPORTED_LANGS;
  protected readonly activeLang = signal<string>(this.transloco.getActiveLang());
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly reason = signal<string | null>(this.route.snapshot.queryParamMap.get('reason'));

  protected readonly form = this.fb.nonNullable.group({
    tenantSlug: [rememberedTenant(), [Validators.required]],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set(this.transloco.translate('login.required'));
      return;
    }

    const value = this.form.getRawValue();
    const input = { ...value, tenantSlug: value.tenantSlug.trim().toLowerCase(), email: value.email.trim() };

    this.loading.set(true);
    this.error.set(null);
    this.session.login(input).subscribe({
      next: () => {
        rememberTenant(input.tenantSlug);
        void this.router.navigateByUrl(this.safeReturnUrl());
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(this.errors.message(err));
        this.form.controls.password.reset('');
      },
    });
  }

  protected changeLang(lang: Lang): void {
    setLang(this.transloco, lang);
    this.activeLang.set(lang);
  }

  /** Solo rutas internas: evita que `returnUrl` redirija a un sitio externo. */
  private safeReturnUrl(): string {
    const url = this.route.snapshot.queryParamMap.get('returnUrl');
    return url && url.startsWith('/') && !url.startsWith('//') && !url.startsWith('/login') ? url : '/';
  }
}
