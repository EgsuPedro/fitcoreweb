import { ChangeDetectionStrategy, Component, OnInit, inject, signal, viewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Onboarding } from '../../core/api/models';
import { SessionService } from '../../core/auth/session.service';
import { BrandingService } from '../../core/branding.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { LogoPicker } from '../../shared/logo-picker';
import { ToastService } from '../../shared/toast';
import { HoursRow, defaultHours, hoursProblem, rowsFromServer, rowsToRequest } from './onboarding-hours';

type Step = 'profile' | 'branch' | 'settings' | 'team' | 'finish';
type Policy = 'RequireFullPayment' | 'AllowUntilOverdue' | 'AllowWithMinimumPayment';

const STEPS: Step[] = ['profile', 'branch', 'settings', 'team', 'finish'];

/**
 * Asistente de configuración inicial del gimnasio. El avance vive en el servidor: se puede cerrar sesión y retomar donde se quedó. Los pasos
 * «datos del gimnasio» y «sucursal principal» son obligatorios; «reglas de acceso» y «equipo» se pueden omitir y hacer después.
 */
@Component({
  selector: 'fc-onboarding',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe, LogoPicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'onboarding.title' | transloco }}</h1>
        <p class="muted">{{ 'onboarding.subtitle' | transloco }}</p>
      </div>
    </header>

    @if (loadError()) { <p class="alert alert-danger" role="alert">{{ loadError() }}</p> }

    @if (state(); as s) {
      <ol class="wizard-steps" [attr.aria-label]="'onboarding.stepsLabel' | transloco">
        @for (code of steps; track code; let i = $index) {
          <li [class.current]="code === step()" [class.done]="isDone(code)">
            <button type="button" class="wizard-step" [attr.aria-current]="code === step() ? 'step' : null" (click)="go(code)">
              <span class="wizard-num">{{ isDone(code) ? '✓' : i + 1 }}</span>
              <span class="wizard-label">{{ 'onboarding.steps.' + code | transloco }}</span>
            </button>
          </li>
        }
      </ol>

      @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

      @switch (step()) {
        @case ('profile') {
          <form class="card section" [formGroup]="profileForm" (ngSubmit)="saveProfile()" novalidate>
            <h2>{{ 'onboarding.profile.title' | transloco }}</h2>
            <p class="muted">{{ 'onboarding.profile.intro' | transloco }}</p>
            <div class="form-grid">
              <label class="field"><span>{{ 'onboarding.profile.name' | transloco }}</span><input formControlName="name" maxlength="200" autocomplete="organization" /></label>
              <label class="field"><span>{{ 'onboarding.profile.email' | transloco }}</span><input type="email" formControlName="contactEmail" maxlength="256" autocomplete="email" /></label>
              <label class="field"><span>{{ 'onboarding.profile.phone' | transloco }}</span><input type="tel" formControlName="contactPhone" maxlength="30" autocomplete="tel" /></label>
              <label class="field"><span>{{ 'onboarding.profile.country' | transloco }}</span><input formControlName="country" maxlength="2" autocapitalize="characters" /><small class="muted">{{ 'onboarding.profile.countryHint' | transloco }}</small></label>
              <label class="field"><span>{{ 'onboarding.profile.timeZone' | transloco }}</span><input formControlName="timeZone" list="onboarding-zones" /></label>
              <label class="field">
                <span>{{ 'onboarding.profile.locale' | transloco }}</span>
                <select formControlName="locale">
                  <option value="es-EC">Español</option>
                  <option value="en">English</option>
                </select>
              </label>
            </div>
            <datalist id="onboarding-zones">
              @for (z of zones; track z) { <option [value]="z"></option> }
            </datalist>
            <p class="muted">{{ 'onboarding.profile.currency' | transloco: { currency: s.profile.currency } }}</p>
            <fieldset class="field">
              <legend>{{ 'onboarding.profile.logo' | transloco }}</legend>
              <fc-logo-picker [currentUrl]="brand.logoUrl()" [disabled]="busy()" (fileChange)="logoFile.set($event)" />
              <small class="muted">{{ 'onboarding.profile.logoHint' | transloco }}</small>
            </fieldset>
            <div class="inline-actions">
              <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'onboarding.saveContinue' | transloco }}</button>
            </div>
          </form>
        }
        @case ('branch') {
          <form class="card section" [formGroup]="branchForm" (ngSubmit)="saveBranch()" novalidate>
            <h2>{{ 'onboarding.branch.title' | transloco }}</h2>
            <p class="muted">{{ 'onboarding.branch.intro' | transloco }}</p>
            <div class="form-grid">
              <label class="field"><span>{{ 'onboarding.branch.name' | transloco }}</span><input formControlName="name" maxlength="200" /></label>
              <label class="field"><span>{{ 'onboarding.branch.address' | transloco }}</span><input formControlName="address" maxlength="300" autocomplete="street-address" /></label>
              <label class="field"><span>{{ 'onboarding.branch.phone' | transloco }}</span><input type="tel" formControlName="phone" maxlength="30" /></label>
              <label class="field"><span>{{ 'onboarding.profile.timeZone' | transloco }}</span><input formControlName="timeZone" list="onboarding-zones" /></label>
            </div>
            <fieldset class="field">
              <legend>{{ 'onboarding.branch.hours' | transloco }}</legend>
              <div class="hours-grid">
                @for (row of hours(); track row.day) {
                  <div class="hours-row" [class.has-problem]="hoursBad() === row.day">
                    <span class="hours-day">{{ 'onboarding.days.' + row.day | transloco }}</span>
                    <label class="check"><input type="checkbox" [checked]="row.closed" (change)="setHours(row.day, { closed: $any($event.target).checked })" /><span>{{ 'onboarding.branch.closed' | transloco }}</span></label>
                    @if (!row.closed) {
                      <input type="time" [value]="row.open" [attr.aria-label]="('onboarding.branch.opens' | transloco) + ' ' + ('onboarding.days.' + row.day | transloco)" (change)="setHours(row.day, { open: $any($event.target).value })" />
                      <input type="time" [value]="row.close" [attr.aria-label]="('onboarding.branch.closes' | transloco) + ' ' + ('onboarding.days.' + row.day | transloco)" (change)="setHours(row.day, { close: $any($event.target).value })" />
                    }
                  </div>
                }
              </div>
              @if (hoursBad() !== null) { <small class="field-error" role="alert">{{ 'onboarding.branch.hoursInvalid' | transloco }}</small> }
            </fieldset>
            <div class="inline-actions">
              <button type="button" class="btn" (click)="go('profile')">{{ 'onboarding.back' | transloco }}</button>
              <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'onboarding.saveContinue' | transloco }}</button>
            </div>
          </form>
        }
        @case ('settings') {
          <form class="card section" [formGroup]="settingsForm" (ngSubmit)="saveSettings()" novalidate>
            <h2>{{ 'onboarding.settings.title' | transloco }}</h2>
            <p class="muted">{{ 'onboarding.settings.intro' | transloco }}</p>
            <fieldset class="field">
              <legend>{{ 'settings.accessPolicy' | transloco }}</legend>
              @for (policy of policies; track policy) {
                <label class="check">
                  <input type="radio" formControlName="accessPolicy" [value]="policy" />
                  <span><strong>{{ 'enums.accessPolicy.' + policy | transloco }}</strong><br /><small class="muted">{{ 'settings.policy.' + policy | transloco }}</small></span>
                </label>
              }
            </fieldset>
            @if (settingsForm.controls.accessPolicy.value === 'AllowWithMinimumPayment') {
              <label class="field"><span>{{ 'settings.minPaymentPercent' | transloco }}</span><input type="number" min="1" max="100" step="1" formControlName="minPaymentPercent" /></label>
            }
            <div class="inline-actions">
              <button type="button" class="btn" (click)="go('branch')">{{ 'onboarding.back' | transloco }}</button>
              <button type="button" class="btn" [disabled]="busy()" (click)="skip('settings')">{{ 'onboarding.skip' | transloco }}</button>
              <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'onboarding.saveContinue' | transloco }}</button>
            </div>
          </form>
        }
        @case ('team') {
          <section class="card section">
            <h2>{{ 'onboarding.team.title' | transloco }}</h2>
            <p class="muted">{{ 'onboarding.team.intro' | transloco }}</p>
            <p><a class="btn" routerLink="/invitations">{{ 'onboarding.team.invite' | transloco }}</a></p>
            <div class="inline-actions">
              <button type="button" class="btn" (click)="go('settings')">{{ 'onboarding.back' | transloco }}</button>
              <button type="button" class="btn" [disabled]="busy()" (click)="skip('team')">{{ 'onboarding.skip' | transloco }}</button>
              <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="resolveTeam()">{{ 'onboarding.continue' | transloco }}</button>
            </div>
          </section>
        }
        @case ('finish') {
          <section class="card section">
            <h2>{{ 'onboarding.finish.title' | transloco }}</h2>
            @if (s.completed) { <p class="alert alert-success">{{ 'onboarding.finish.done' | transloco }}</p> }
            <dl class="summary">
              <dt>{{ 'onboarding.profile.name' | transloco }}</dt><dd>{{ s.profile.name }}</dd>
              <dt>{{ 'onboarding.profile.email' | transloco }}</dt><dd>{{ s.profile.contactEmail || '—' }}</dd>
              <dt>{{ 'onboarding.branch.name' | transloco }}</dt><dd>{{ s.branch?.name || '—' }}</dd>
              <dt>{{ 'onboarding.branch.address' | transloco }}</dt><dd>{{ s.branch?.address || '—' }}</dd>
              @for (code of optionalSteps; track code) {
                <dt>{{ 'onboarding.steps.' + code | transloco }}</dt>
                <dd>{{ 'onboarding.status.' + statusOf(code) | transloco }}</dd>
              }
            </dl>
            @if (!s.canComplete) { <p class="alert alert-warning">{{ 'onboarding.finish.missing' | transloco }}</p> }
            <div class="inline-actions">
              <button type="button" class="btn" (click)="go('team')">{{ 'onboarding.back' | transloco }}</button>
              @if (s.completed) {
                <a class="btn btn-primary" routerLink="/">{{ 'onboarding.finish.goHome' | transloco }}</a>
              } @else {
                <button type="button" class="btn btn-primary" [disabled]="busy() || !s.canComplete" (click)="finish()">{{ 'onboarding.finish.action' | transloco }}</button>
              }
            </div>
          </section>
        }
      }
    }
  `,
})
export class OnboardingPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);
  protected readonly brand = inject(BrandingService);

  private readonly picker = viewChild(LogoPicker);
  protected readonly steps = STEPS;
  protected readonly optionalSteps: ('settings' | 'team')[] = ['settings', 'team'];
  protected readonly policies: Policy[] = ['RequireFullPayment', 'AllowUntilOverdue', 'AllowWithMinimumPayment'];
  protected readonly zones = ['America/Guayaquil', 'America/Bogota', 'America/Lima', 'America/Mexico_City', 'America/Santiago', 'America/Argentina/Buenos_Aires', 'America/New_York', 'Europe/Madrid', 'UTC'];

  protected readonly state = signal<Onboarding | null>(null);
  protected readonly step = signal<Step>('profile');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly logoFile = signal<File | null>(null);
  protected readonly hours = signal<HoursRow[]>(defaultHours());
  protected readonly hoursBad = signal<number | null>(null);

  protected readonly profileForm = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    contactEmail: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    contactPhone: ['', [Validators.pattern(/^[+0-9][0-9 ()-]{6,28}$/)]],
    country: ['EC', [Validators.required, Validators.pattern(/^[A-Za-z]{2}$/)]],
    timeZone: ['America/Guayaquil', [Validators.required]],
    locale: ['es-EC'],
  });

  protected readonly branchForm = this.fb.nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(200)]],
    address: ['', [Validators.required, Validators.maxLength(300)]],
    phone: ['', [Validators.pattern(/^[+0-9][0-9 ()-]{6,28}$/)]],
    timeZone: ['America/Guayaquil', [Validators.required]],
  });

  protected readonly settingsForm = this.fb.nonNullable.group({
    accessPolicy: ['AllowUntilOverdue' as Policy],
    minPaymentPercent: [50, [Validators.min(1), Validators.max(100)]],
  });

  private currentSettings: { dueDays: number; maxDiscountPercent: number } = { dueDays: 7, maxDiscountPercent: 0 };

  ngOnInit(): void {
    this.api.onboarding().subscribe({
      next: (s) => {
        this.apply(s);
        this.step.set(s.currentStep as Step);
        this.brand.refresh();
      },
      error: (err: unknown) => this.loadError.set(this.errors.message(err)),
    });
    this.api.commercialSettings().subscribe({
      next: (c) => {
        this.currentSettings = { dueDays: c.dueDays, maxDiscountPercent: c.maxDiscountPercent };
        this.settingsForm.reset({ accessPolicy: c.accessPolicy as Policy, minPaymentPercent: c.minPaymentPercent || 50 });
      },
      error: () => undefined, // sin permiso para verlas: el paso se puede omitir
    });
  }

  protected isDone(code: Step): boolean {
    if (code === 'finish') {
      return this.state()?.completed ?? false;
    }
    return this.state()?.steps.find((s) => s.code === code)?.status !== 'Pending';
  }

  protected statusOf(code: 'settings' | 'team'): string {
    return this.state()?.steps.find((s) => s.code === code)?.status ?? 'Pending';
  }

  protected go(step: Step): void {
    this.error.set(null);
    this.step.set(step);
  }

  protected setHours(day: number, change: Partial<HoursRow>): void {
    this.hours.update((rows) => rows.map((r) => (r.day === day ? { ...r, ...change } : r)));
    this.hoursBad.set(null);
  }

  protected saveProfile(): void {
    if (this.profileForm.invalid) {
      this.profileForm.markAllAsTouched();
      this.error.set(this.errors.text('onboarding.invalidForm'));
      return;
    }
    const v = this.profileForm.getRawValue();
    this.run(
      this.api.saveOnboardingProfile({ ...v, country: v.country.toUpperCase(), contactPhone: v.contactPhone || null }),
      'branch',
      () => this.uploadLogoIfAny(),
    );
  }

  protected saveBranch(): void {
    const problem = hoursProblem(this.hours());
    if (this.branchForm.invalid || problem) {
      this.branchForm.markAllAsTouched();
      this.hoursBad.set(problem?.day ?? null);
      this.error.set(this.errors.text('onboarding.invalidForm'));
      return;
    }
    const v = this.branchForm.getRawValue();
    this.run(this.api.saveOnboardingBranch({ ...v, phone: v.phone || null, hours: rowsToRequest(this.hours()) }), 'settings');
  }

  protected saveSettings(): void {
    if (this.settingsForm.invalid) {
      this.settingsForm.markAllAsTouched();
      this.error.set(this.errors.text('onboarding.invalidForm'));
      return;
    }
    const v = this.settingsForm.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.api
      .updateCommercialSettings({
        accessPolicy: v.accessPolicy,
        minPaymentPercent: Number(v.minPaymentPercent),
        dueDays: this.currentSettings.dueDays,
        maxDiscountPercent: this.currentSettings.maxDiscountPercent,
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.resolve('settings', false);
        },
        error: (err: unknown) => {
          this.busy.set(false);
          this.error.set(this.errors.message(err));
        },
      });
  }

  protected skip(step: 'settings' | 'team'): void {
    this.resolve(step, true);
  }

  protected resolveTeam(): void {
    this.resolve('team', false);
  }

  protected finish(): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.completeOnboarding().subscribe({
      next: (s) => {
        this.busy.set(false);
        this.apply(s);
        this.toast.success('onboarding.finish.toast');
        this.session.refreshProfile().subscribe({ error: () => undefined });
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  private resolve(step: 'settings' | 'team', skip: boolean): void {
    this.run(this.api.resolveOnboardingStep(step, skip), step === 'settings' ? 'team' : 'finish');
  }

  private run(call: ReturnType<ApiService['onboarding']>, next: Step, after?: () => void): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: (s) => {
        this.busy.set(false);
        this.apply(s);
        after?.();
        this.step.set(next);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  private uploadLogoIfAny(): void {
    const file = this.logoFile();
    if (!file) {
      return;
    }
    this.api.uploadLogo(file).subscribe({
      next: () => {
        this.logoFile.set(null);
        this.picker()?.reset();
        this.brand.refresh();
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  private apply(s: Onboarding): void {
    this.state.set(s);
    this.profileForm.reset({
      name: s.profile.name,
      contactEmail: s.profile.contactEmail ?? '',
      contactPhone: s.profile.contactPhone ?? '',
      country: s.profile.country,
      timeZone: s.profile.timeZone,
      locale: s.profile.locale === 'en' ? 'en' : 'es-EC',
    });
    if (s.branch) {
      this.branchForm.reset({ name: s.branch.name, address: s.branch.address ?? '', phone: s.branch.phone ?? '', timeZone: s.branch.timeZone });
      this.hours.set(rowsFromServer(s.branch.hours));
    } else {
      this.branchForm.patchValue({ timeZone: s.profile.timeZone });
    }
  }

}
