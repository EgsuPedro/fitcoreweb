import { isPlatformBrowser } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, PLATFORM_ID, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { InvitationPreview, PublicPlan, VerifiedRegistration } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { pageSeo } from '../../core/page-seo';
import { SLUG_PATTERN, matchFields, passwordPolicy, suggestSlug } from '../../shared/account-forms';
import { LEGAL_VERSION } from './legal-texts';

/**
 * Lee el token del enlace y lo quita de la barra de direcciones de inmediato: así no queda en el historial, ni se copia por error al compartir la pantalla, ni viaja
 * en un «referer». El token vive solo en memoria mientras se usa.
 */
function takeToken(route: ActivatedRoute, router: Router): string | null {
  const token = route.snapshot.queryParamMap.get('token');
  if (token !== null) {
    void router.navigate([], { relativeTo: route, queryParams: { token: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }
  return token && token.length <= 200 ? token : null;
}

/** Registro público de un gimnasio. El navegador solo envía el código del plan: el precio y las condiciones los decide el servidor. */
@Component({
  selector: 'fc-public-register',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        @if (sentTo(); as email) {
          <h1>{{ 'site.signup.sentTitle' | transloco }}</h1>
          <div class="alert alert-success" role="status">{{ 'site.signup.sentText' | transloco: { email: email } }}</div>
          <p class="muted">{{ 'site.signup.sentHint' | transloco }}</p>
          @if (resendNote()) { <p class="alert alert-info" role="status">{{ 'site.signup.resent' | transloco }}</p> }
          @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }
          <button type="button" class="btn" [disabled]="cooldown() > 0 || busy()" (click)="resend(email)">
            {{ cooldown() > 0 ? ('site.signup.resendWait' | transloco: { seconds: cooldown() }) : ('site.signup.resend' | transloco) }}
          </button>
        } @else {
          <h1>{{ 'site.signup.title' | transloco }}</h1>
          <p class="pub-lead">{{ 'site.signup.lead' | transloco }}</p>

          @if (loadingPlans()) { <p class="muted" role="status">{{ 'common.loading' | transloco }}</p> }
          @if (!loadingPlans() && plans().length === 0) {
            <p class="alert alert-info">{{ 'site.signup.noPlans' | transloco }} <a routerLink="/contacto" [queryParams]="{ tipo: 'demo' }">{{ 'site.home.ctaDemo' | transloco }}</a></p>
          }

          @if (plans().length > 0) {
            <form class="card section" [formGroup]="form" (ngSubmit)="submit()" novalidate>
              @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }
              <label class="field">
                <span>{{ 'site.signup.plan' | transloco }}</span>
                <select formControlName="planCode">
                  @for (plan of plans(); track plan.code) { <option [value]="plan.code">{{ plan.name }}{{ plan.trialDays > 0 ? ' · ' + ('site.plans.trial' | transloco: { days: plan.trialDays }) : '' }}</option> }
                </select>
                <small class="muted">{{ 'site.signup.planHint' | transloco }} <a routerLink="/planes" target="_blank">{{ 'site.nav.plans' | transloco }}</a></small>
              </label>
              <div class="form-grid">
                <label class="field">
                  <span>{{ 'site.signup.ownerName' | transloco }}</span>
                  <input formControlName="ownerFullName" maxlength="200" autocomplete="name" [attr.aria-invalid]="bad('ownerFullName')" />
                  @if (bad('ownerFullName')) { <small class="field-error" role="alert">{{ 'site.contact.required' | transloco }}</small> }
                </label>
                <label class="field">
                  <span>{{ 'site.signup.email' | transloco }}</span>
                  <input formControlName="email" type="email" maxlength="256" autocomplete="email" [attr.aria-invalid]="bad('email')" />
                  @if (bad('email')) { <small class="field-error" role="alert">{{ 'site.contact.emailInvalid' | transloco }}</small> }
                </label>
                <label class="field">
                  <span>{{ 'site.signup.gymName' | transloco }}</span>
                  <input formControlName="gymName" maxlength="200" autocomplete="organization" [attr.aria-invalid]="bad('gymName')" />
                  @if (bad('gymName')) { <small class="field-error" role="alert">{{ 'site.contact.required' | transloco }}</small> }
                </label>
                <label class="field">
                  <span>{{ 'site.signup.slug' | transloco }}</span>
                  <input formControlName="slug" maxlength="40" autocapitalize="none" spellcheck="false" [attr.aria-invalid]="bad('slug')" (input)="slugEdited = true" />
                  <small class="muted">{{ 'site.signup.slugHint' | transloco }}</small>
                  @if (form.controls.slug.errors?.['taken']) { <small class="field-error" role="alert">{{ 'errors.registration.slug_taken' | transloco }}</small> }
                  @else if (bad('slug')) { <small class="field-error" role="alert">{{ 'site.signup.slugInvalid' | transloco }}</small> }
                </label>
              </div>
              <label class="field">
                <span>{{ 'site.signup.password' | transloco }}</span>
                <input formControlName="password" [type]="show() ? 'text' : 'password'" autocomplete="new-password" [attr.aria-invalid]="bad('password')" />
                <small class="muted">{{ 'site.password.hint' | transloco }}</small>
                @if (bad('password')) { <small class="field-error" role="alert">{{ (form.controls.password.errors?.['passwordPolicy'] ? 'site.password.invalid' : 'site.contact.required') | transloco }}</small> }
              </label>
              <label class="check"><input type="checkbox" [checked]="show()" (change)="show.set($any($event.target).checked)" /><span>{{ 'site.password.show' | transloco }}</span></label>
              <label class="check">
                <input type="checkbox" formControlName="accept" [attr.aria-invalid]="bad('accept')" />
                <span>
                  {{ 'site.signup.accept' | transloco }}
                  <a routerLink="/terminos" target="_blank">{{ 'site.footer.terms' | transloco }}</a> {{ 'site.signup.and' | transloco }}
                  <a routerLink="/privacidad" target="_blank">{{ 'site.footer.privacy' | transloco }}</a>
                </span>
              </label>
              @if (bad('accept')) { <small class="field-error" role="alert">{{ 'site.signup.acceptRequired' | transloco }}</small> }
              <div class="inline-actions">
                <button type="submit" class="btn btn-primary btn-lg" [disabled]="busy()">{{ 'site.signup.submit' | transloco }}</button>
              </div>
              <p class="muted">{{ 'site.signup.noCharge' | transloco }}</p>
            </form>
          }
        }
      </div>
    </section>
  `,
})
export class PublicRegister implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly route = inject(ActivatedRoute);
  private readonly transloco = inject(TranslocoService);
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  protected readonly plans = signal<PublicPlan[]>([]);
  protected readonly loadingPlans = signal(true);
  protected readonly busy = signal(false);
  protected readonly show = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly sentTo = signal<string | null>(null);
  protected readonly cooldown = signal(0);
  protected readonly resendNote = signal(false);
  protected slugEdited = false;
  private timer: ReturnType<typeof setInterval> | null = null;

  protected readonly form = this.fb.nonNullable.group({
    planCode: ['', [Validators.required]],
    ownerFullName: ['', [Validators.required, Validators.maxLength(200)]],
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    gymName: ['', [Validators.required, Validators.maxLength(200)]],
    slug: ['', [Validators.required, Validators.pattern(SLUG_PATTERN)]],
    password: ['', [Validators.required, Validators.maxLength(200), passwordPolicy]],
    accept: [false, [Validators.requiredTrue]],
  });

  protected readonly canSubmit = computed(() => this.plans().length > 0);

  constructor() {
    pageSeo('/registro', 'site.signup.seoTitle', 'site.signup.seoDescription');
    this.form.controls.gymName.valueChanges.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((name) => {
      if (!this.slugEdited) {
        this.form.controls.slug.setValue(suggestSlug(name));
      }
    });
    this.destroyRef.onDestroy(() => this.stopTimer());
  }

  ngOnInit(): void {
    if (!this.browser) {
      return; // el prerenderizado no tiene servidor al que preguntar: los planes se cargan en el navegador
    }
    this.api.publicPlans().subscribe({
      next: (all) => {
        // Solo se ofrecen los que se pueden contratar por registro (prueba gratuita o precio 0); el resto se pide a ventas.
        const selfService = all.filter((p) => p.selfService);
        this.plans.set(selfService);
        const wanted = this.route.snapshot.queryParamMap.get('plan');
        this.form.controls.planCode.setValue(selfService.find((p) => p.code === wanted)?.code ?? selfService[0]?.code ?? '');
        this.loadingPlans.set(false);
      },
      error: () => this.loadingPlans.set(false),
    });
  }

  protected bad(name: keyof typeof this.form.controls): boolean {
    const control = this.form.controls[name];
    return control.invalid && (control.touched || control.dirty);
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.api
      .registerGym({
        ownerFullName: v.ownerFullName.trim(),
        email: v.email.trim(),
        password: v.password,
        gymName: v.gymName.trim(),
        slug: v.slug.trim().toLowerCase(),
        planCode: v.planCode,
        termsVersion: LEGAL_VERSION,
        privacyVersion: LEGAL_VERSION,
        locale: this.transloco.getActiveLang() === 'en' ? 'en' : 'es',
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.form.controls.password.reset('');
          this.sentTo.set(v.email.trim());
          this.startCooldown();
        },
        error: (err: unknown) => {
          this.busy.set(false);
          const code = this.errors.code(err);
          if (code === 'registration.slug_taken') {
            this.form.controls.slug.setErrors({ taken: true });
            this.form.controls.slug.markAsTouched();
          } else {
            this.error.set(this.errors.message(err));
          }
        },
      });
  }

  protected resend(email: string): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.resendVerification(email).subscribe({
      next: () => {
        this.busy.set(false);
        this.resendNote.set(true);
        this.startCooldown();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  private startCooldown(): void {
    this.stopTimer();
    this.cooldown.set(60);
    this.timer = setInterval(() => {
      this.cooldown.update((n) => Math.max(0, n - 1));
      if (this.cooldown() === 0) {
        this.stopTimer();
      }
    }, 1000);
  }

  private stopTimer(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}

/** Confirmación del correo: gasta el enlace una sola vez y, si falla, explica por qué y ofrece pedir otro. */
@Component({
  selector: 'fc-public-verify',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        <h1>{{ 'site.verify.title' | transloco }}</h1>
        @switch (state()) {
          @case ('working') { <p class="muted" role="status">{{ 'site.verify.working' | transloco }}</p> }
          @case ('done') {
            @if (result(); as r) {
              <div class="alert alert-success" role="status">
                <strong>{{ 'site.verify.doneTitle' | transloco }}</strong>
                <p>{{ 'site.verify.doneText' | transloco: { gym: r.tenantSlug } }}</p>
                @if (r.trialDays > 0) { <p>{{ 'site.verify.trial' | transloco: { plan: r.planName, days: r.trialDays } }}</p> }
              </div>
              <dl class="summary">
                <dt>{{ 'site.verify.gym' | transloco }}</dt><dd><code>{{ r.tenantSlug }}</code></dd>
                <dt>{{ 'site.signup.email' | transloco }}</dt><dd>{{ r.ownerEmail }}</dd>
              </dl>
              <a class="btn btn-primary btn-lg" routerLink="/login" [queryParams]="{ gimnasio: r.tenantSlug }">{{ 'site.verify.signIn' | transloco }}</a>
            }
          }
          @default {
            <p class="alert alert-danger" role="alert">{{ error() }}</p>
            @if (offerResend()) {
              <form class="card section" [formGroup]="resendForm" (ngSubmit)="resend()" novalidate>
                <p>{{ 'site.verify.resendIntro' | transloco }}</p>
                <label class="field">
                  <span>{{ 'site.signup.email' | transloco }}</span>
                  <input formControlName="email" type="email" autocomplete="email" />
                </label>
                @if (resent()) { <p class="alert alert-info" role="status">{{ 'site.verify.resent' | transloco }}</p> }
                <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'site.signup.resend' | transloco }}</button>
              </form>
            } @else {
              <a class="btn" routerLink="/contacto">{{ 'site.nav.contact' | transloco }}</a>
            }
          }
        }
      </div>
    </section>
  `,
})
export class PublicVerifyEmail implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  protected readonly state = signal<'working' | 'done' | 'failed'>('working');
  protected readonly result = signal<VerifiedRegistration | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly offerResend = signal(false);
  protected readonly resent = signal(false);
  protected readonly busy = signal(false);
  protected readonly resendForm = this.fb.nonNullable.group({ email: ['', [Validators.required, Validators.email]] });

  constructor() {
    pageSeo('/verificar-correo', 'site.verify.title', 'site.verify.seoDescription', undefined, { index: false });
  }

  ngOnInit(): void {
    if (!this.browser) {
      return;
    }
    const token = takeToken(this.route, this.router);
    if (!token) {
      this.fail('registration.token_invalid');
      return;
    }
    this.api.verifyEmail(token).subscribe({
      next: (r) => {
        this.result.set(r);
        this.state.set('done');
      },
      error: (err: unknown) => this.fail(this.errors.code(err), this.errors.message(err)),
    });
  }

  protected resend(): void {
    if (this.resendForm.invalid) {
      this.resendForm.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.api.resendVerification(this.resendForm.getRawValue().email.trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.resent.set(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  private fail(code: string | null, message?: string): void {
    this.state.set('failed');
    this.error.set(message ?? this.errors.text(`errors.${code ?? 'generic'}`));
    // Un enlace malo, vencido o ya usado se resuelve pidiendo otro; los demás casos (prueba usada, identificador ocupado…) piden hablar con nosotros.
    this.offerResend.set(code === 'registration.token_invalid' || code === 'registration.expired');
  }
}

/** «Olvidé mi contraseña»: pide el gimnasio y el correo. La respuesta es siempre la misma exista o no la cuenta. */
@Component({
  selector: 'fc-public-forgot',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        <h1>{{ 'site.forgot.title' | transloco }}</h1>
        @if (sent()) {
          <div class="alert alert-success" role="status">{{ 'site.forgot.sent' | transloco }}</div>
          <a class="btn" routerLink="/login">{{ 'site.verify.signIn' | transloco }}</a>
        } @else {
          <p class="pub-lead">{{ 'site.forgot.lead' | transloco }}</p>
          <form class="card section" [formGroup]="form" (ngSubmit)="submit()" novalidate>
            @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }
            <label class="field">
              <span>{{ 'login.tenant' | transloco }}</span>
              <input formControlName="tenantSlug" autocapitalize="none" spellcheck="false" autocomplete="organization" />
              <small class="muted">{{ 'login.tenantHint' | transloco }}</small>
            </label>
            <label class="field">
              <span>{{ 'site.signup.email' | transloco }}</span>
              <input formControlName="email" type="email" autocomplete="email" />
            </label>
            <div class="inline-actions">
              <a class="btn" routerLink="/login">{{ 'common.cancel' | transloco }}</a>
              <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'site.forgot.submit' | transloco }}</button>
            </div>
          </form>
        }
      </div>
    </section>
  `,
})
export class PublicForgotPassword {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder);

  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = this.fb.nonNullable.group({
    tenantSlug: [this.route.snapshot.queryParamMap.get('gimnasio') ?? '', [Validators.required]],
    email: ['', [Validators.required, Validators.email]],
  });

  constructor() {
    pageSeo('/olvide-mi-clave', 'site.forgot.title', 'site.forgot.seoDescription', undefined, { index: false });
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.error.set(this.errors.text('login.required'));
      return;
    }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.api.forgotPassword(v.tenantSlug.trim().toLowerCase(), v.email.trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.sent.set(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}

/** Elegir una contraseña nueva con el enlace del correo. Si la contraseña no cumple la política, el enlace no se gasta y se puede reintentar. */
@Component({
  selector: 'fc-public-reset',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        <h1>{{ 'site.reset.title' | transloco }}</h1>
        @if (done()) {
          <div class="alert alert-success" role="status">{{ 'site.reset.done' | transloco }}</div>
          <a class="btn btn-primary btn-lg" routerLink="/login">{{ 'site.verify.signIn' | transloco }}</a>
        } @else if (!token()) {
          <p class="alert alert-danger" role="alert">{{ 'errors.auth.reset_token_invalid' | transloco }}</p>
          <a class="btn btn-primary" routerLink="/olvide-mi-clave">{{ 'site.reset.askAgain' | transloco }}</a>
        } @else {
          <form class="card section" [formGroup]="form" (ngSubmit)="submit()" novalidate>
            @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }
            @if (expired()) { <a routerLink="/olvide-mi-clave">{{ 'site.reset.askAgain' | transloco }}</a> }
            <label class="field">
              <span>{{ 'site.reset.password' | transloco }}</span>
              <input formControlName="password" [type]="show() ? 'text' : 'password'" autocomplete="new-password" [attr.aria-invalid]="bad('password')" />
              <small class="muted">{{ 'site.password.hint' | transloco }}</small>
              @if (bad('password')) { <small class="field-error" role="alert">{{ (form.controls.password.errors?.['passwordPolicy'] ? 'site.password.invalid' : 'site.contact.required') | transloco }}</small> }
            </label>
            <label class="field">
              <span>{{ 'site.reset.confirm' | transloco }}</span>
              <input formControlName="confirm" [type]="show() ? 'text' : 'password'" autocomplete="new-password" [attr.aria-invalid]="bad('confirm')" />
              @if (bad('confirm')) { <small class="field-error" role="alert">{{ 'site.password.mismatch' | transloco }}</small> }
            </label>
            <label class="check"><input type="checkbox" [checked]="show()" (change)="show.set($any($event.target).checked)" /><span>{{ 'site.password.show' | transloco }}</span></label>
            <div class="inline-actions">
              <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'site.reset.submit' | transloco }}</button>
            </div>
          </form>
        }
      </div>
    </section>
  `,
})
export class PublicResetPassword implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  protected readonly token = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly done = signal(false);
  protected readonly show = signal(false);
  protected readonly expired = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly form = this.fb.nonNullable.group(
    { password: ['', [Validators.required, Validators.maxLength(200), passwordPolicy]], confirm: ['', [Validators.required]] },
    { validators: matchFields('password', 'confirm') },
  );

  constructor() {
    pageSeo('/restablecer-clave', 'site.reset.title', 'site.reset.seoDescription', undefined, { index: false });
  }

  ngOnInit(): void {
    if (this.browser) {
      this.token.set(takeToken(this.route, this.router));
    }
  }

  protected bad(name: 'password' | 'confirm'): boolean {
    const control = this.form.controls[name];
    return control.invalid && (control.touched || control.dirty);
  }

  protected submit(): void {
    const token = this.token();
    if (!token || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    this.busy.set(true);
    this.error.set(null);
    this.api.resetPassword(token, this.form.getRawValue().password).subscribe({
      next: () => {
        this.busy.set(false);
        this.done.set(true);
        this.token.set(null);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
        this.expired.set(this.errors.code(err) === 'auth.reset_token_invalid');
      },
    });
  }
}

/** Aceptar una invitación de personal: muestra a qué gimnasio y con qué rol (sin gastar el enlace) y pide el nombre y la contraseña. */
@Component({
  selector: 'fc-public-accept-invitation',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        <h1>{{ 'site.invite.title' | transloco }}</h1>
        @if (accepted(); as a) {
          <div class="alert alert-success" role="status">{{ 'site.invite.done' | transloco }}</div>
          <a class="btn btn-primary btn-lg" routerLink="/login" [queryParams]="{ gimnasio: a.tenantSlug }">{{ 'site.verify.signIn' | transloco }}</a>
        } @else if (loading()) {
          <p class="muted" role="status">{{ 'site.verify.working' | transloco }}</p>
        } @else if (!preview()) {
          <p class="alert alert-danger" role="alert">{{ error() }}</p>
          <a class="btn" routerLink="/contacto">{{ 'site.nav.contact' | transloco }}</a>
        } @else {
          @if (preview(); as p) {
            <p class="pub-lead">{{ 'site.invite.lead' | transloco: { gym: p.gymName, role: p.roleName } }}</p>
            <p class="muted">{{ 'site.invite.forEmail' | transloco: { email: p.email } }}</p>
          }
          <form class="card section" [formGroup]="form" (ngSubmit)="submit()" novalidate>
            @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }
            <label class="field">
              <span>{{ 'site.signup.ownerName' | transloco }}</span>
              <input formControlName="fullName" maxlength="200" autocomplete="name" [attr.aria-invalid]="bad('fullName')" />
              @if (bad('fullName')) { <small class="field-error" role="alert">{{ 'site.contact.required' | transloco }}</small> }
            </label>
            <label class="field">
              <span>{{ 'site.reset.password' | transloco }}</span>
              <input formControlName="password" [type]="show() ? 'text' : 'password'" autocomplete="new-password" [attr.aria-invalid]="bad('password')" />
              <small class="muted">{{ 'site.password.hint' | transloco }}</small>
              @if (bad('password')) { <small class="field-error" role="alert">{{ (form.controls.password.errors?.['passwordPolicy'] ? 'site.password.invalid' : 'site.contact.required') | transloco }}</small> }
            </label>
            <label class="field">
              <span>{{ 'site.reset.confirm' | transloco }}</span>
              <input formControlName="confirm" [type]="show() ? 'text' : 'password'" autocomplete="new-password" [attr.aria-invalid]="bad('confirm')" />
              @if (bad('confirm')) { <small class="field-error" role="alert">{{ 'site.password.mismatch' | transloco }}</small> }
            </label>
            <label class="check"><input type="checkbox" [checked]="show()" (change)="show.set($any($event.target).checked)" /><span>{{ 'site.password.show' | transloco }}</span></label>
            <div class="inline-actions">
              <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'site.invite.submit' | transloco }}</button>
            </div>
          </form>
        }
      </div>
    </section>
  `,
})
export class PublicAcceptInvitation implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));

  private token: string | null = null;
  protected readonly loading = signal(true);
  protected readonly busy = signal(false);
  protected readonly show = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly preview = signal<InvitationPreview | null>(null);
  protected readonly accepted = signal<{ tenantSlug: string } | null>(null);
  protected readonly form = this.fb.nonNullable.group(
    {
      fullName: ['', [Validators.required, Validators.maxLength(200)]],
      password: ['', [Validators.required, Validators.maxLength(200), passwordPolicy]],
      confirm: ['', [Validators.required]],
    },
    { validators: matchFields('password', 'confirm') },
  );

  constructor() {
    pageSeo('/aceptar-invitacion', 'site.invite.title', 'site.invite.seoDescription', undefined, { index: false });
  }

  ngOnInit(): void {
    if (!this.browser) {
      return;
    }
    this.token = takeToken(this.route, this.router);
    if (!this.token) {
      this.loading.set(false);
      this.error.set(this.errors.text('errors.invitations.token_invalid'));
      return;
    }
    this.api.previewInvitation(this.token).subscribe({
      next: (p) => {
        this.preview.set(p);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected bad(name: 'fullName' | 'password' | 'confirm'): boolean {
    const control = this.form.controls[name];
    return control.invalid && (control.touched || control.dirty);
  }

  protected submit(): void {
    if (!this.token || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.api.acceptInvitation(this.token, v.fullName.trim(), v.password).subscribe({
      next: (r) => {
        this.busy.set(false);
        this.accepted.set(r);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
