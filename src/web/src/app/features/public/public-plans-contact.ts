import { isPlatformBrowser } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, PLATFORM_ID, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { PublicPlan } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { pageSeo } from '../../core/page-seo';
import { FcMoney } from '../../shared/format.pipes';
import { LEGAL_VERSION } from './legal-texts';

/** Planes y precios: salen siempre del catálogo real del servidor (nunca hay precios escritos a mano en el sitio). */
@Component({
  selector: 'fc-public-plans',
  imports: [RouterLink, TranslocoPipe, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap">
        <h1>{{ 'site.plans.title' | transloco }}</h1>
        <p class="pub-lead">{{ 'site.plans.lead' | transloco }}</p>

        @if (error()) { <p class="alert alert-danger" role="alert">{{ 'site.plans.error' | transloco }}</p> }
        @if (loading()) { <p class="muted" role="status">{{ 'common.loading' | transloco }}</p> }

        @if (!loading() && !error() && plans().length === 0) {
          <p class="alert alert-info">{{ 'site.plans.empty' | transloco }}</p>
        }

        <ul class="pub-plans">
          @for (plan of plans(); track plan.code) {
            <li class="pub-plan">
              <h2>{{ plan.name }}</h2>
              @if (plan.description) { <p class="muted">{{ plan.description }}</p> }
              <p class="pub-price">
                @if (plan.price === 0) { {{ 'site.plans.free' | transloco }} } @else { {{ plan.price | fcMoney }} }
                <small>/ {{ 'site.plans.period.' + plan.billingPeriod | transloco }}</small>
              </p>
              @if (plan.trialDays > 0) { <p><span class="badge badge-ok">{{ 'site.plans.trial' | transloco: { days: plan.trialDays } }}</span></p> }
              <h3>{{ 'site.plans.includes' | transloco }}</h3>
              <ul class="pub-ticks">
                @for (feature of plan.features; track feature.code) { <li>{{ feature.name }}</li> }
                @for (limit of plan.limits; track limit.code) {
                  <li>{{ limit.name }}: {{ limit.value === null ? ('site.plans.unlimited' | transloco) : limit.value + ' ' + limit.unit }}</li>
                }
              </ul>
              @if (plan.selfService) {
                <a class="btn btn-primary" routerLink="/registro" [queryParams]="{ plan: plan.code }">{{ 'site.plans.start' | transloco }}</a>
              } @else {
                <a class="btn" routerLink="/contacto" [queryParams]="{ tipo: 'ventas', plan: plan.code }">{{ 'site.plans.sales' | transloco }}</a>
              }
            </li>
          }
        </ul>
        <p class="muted">{{ 'site.plans.note' | transloco }}</p>
      </div>
    </section>
  `,
})
export class PublicPlans implements OnInit {
  private readonly api = inject(ApiService);
  private readonly browser = isPlatformBrowser(inject(PLATFORM_ID));
  protected readonly plans = signal<PublicPlan[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal(false);

  constructor() {
    pageSeo('/planes', 'site.plans.seoTitle', 'site.plans.seoDescription');
  }

  ngOnInit(): void {
    if (!this.browser) {
      // En el prerenderizado no hay servidor al que preguntar: los planes se cargan en el navegador.
      return;
    }
    this.api.publicPlans().subscribe({
      next: (plans) => {
        this.plans.set(plans);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }
}

/** Formulario de contacto, de demostración y de «hablar con ventas». Pide consentimiento explícito y lleva un campo trampa contra robots. */
@Component({
  selector: 'fc-public-contact',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        <h1>{{ 'site.contact.title' | transloco }}</h1>
        <p class="pub-lead">{{ 'site.contact.lead' | transloco }}</p>

        @if (sent()) {
          <div class="alert alert-success" role="status" tabindex="-1" #done>
            <strong>{{ 'site.contact.sentTitle' | transloco }}</strong>
            <p>{{ 'site.contact.sentText' | transloco }}</p>
          </div>
          <a class="btn" routerLink="/">{{ 'site.contact.backHome' | transloco }}</a>
        } @else {
          <form class="card section" [formGroup]="form" (ngSubmit)="send()" novalidate>
            @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }
            <fieldset class="field">
              <legend>{{ 'site.contact.kind' | transloco }}</legend>
              <label class="check"><input type="radio" formControlName="kind" value="contact" /><span>{{ 'site.contact.kindContact' | transloco }}</span></label>
              <label class="check"><input type="radio" formControlName="kind" value="demo" /><span>{{ 'site.contact.kindDemo' | transloco }}</span></label>
            </fieldset>
            <div class="form-grid">
              <label class="field">
                <span>{{ 'site.contact.name' | transloco }}</span>
                <input formControlName="name" maxlength="120" autocomplete="name" [attr.aria-invalid]="invalid('name')" />
                @if (invalid('name')) { <small class="field-error" role="alert">{{ 'site.contact.required' | transloco }}</small> }
              </label>
              <label class="field">
                <span>{{ 'site.contact.email' | transloco }}</span>
                <input formControlName="email" type="email" maxlength="256" autocomplete="email" [attr.aria-invalid]="invalid('email')" />
                @if (invalid('email')) { <small class="field-error" role="alert">{{ 'site.contact.emailInvalid' | transloco }}</small> }
              </label>
              <label class="field">
                <span>{{ 'site.contact.phone' | transloco }}</span>
                <input formControlName="phone" type="tel" maxlength="30" autocomplete="tel" [attr.aria-invalid]="invalid('phone')" />
                @if (invalid('phone')) { <small class="field-error" role="alert">{{ 'site.contact.phoneInvalid' | transloco }}</small> }
              </label>
              <label class="field">
                <span>{{ 'site.contact.gym' | transloco }}</span>
                <input formControlName="gymName" maxlength="200" autocomplete="organization" />
              </label>
            </div>
            <label class="field">
              <span>{{ 'site.contact.message' | transloco }}</span>
              <textarea formControlName="message" rows="5" maxlength="1000" [attr.aria-invalid]="invalid('message')"></textarea>
              @if (invalid('message')) { <small class="field-error" role="alert">{{ 'site.contact.messageInvalid' | transloco }}</small> }
            </label>
            <!-- Campo trampa: una persona no lo ve; un robot suele llenarlo y el servidor descarta el mensaje. -->
            <div class="hp-field" aria-hidden="true">
              <label>Website <input formControlName="website" tabindex="-1" autocomplete="off" /></label>
            </div>
            <label class="check">
              <input type="checkbox" formControlName="acceptPrivacy" [attr.aria-invalid]="invalid('acceptPrivacy')" />
              <span>{{ 'site.contact.consent' | transloco }} <a routerLink="/privacidad" target="_blank">{{ 'site.footer.privacy' | transloco }}</a></span>
            </label>
            @if (invalid('acceptPrivacy')) { <small class="field-error" role="alert">{{ 'site.contact.consentRequired' | transloco }}</small> }
            <div class="inline-actions">
              <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'site.contact.send' | transloco }}</button>
            </div>
          </form>
        }
      </div>
    </section>
  `,
})
export class PublicContact {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly route = inject(ActivatedRoute);
  private readonly fb = inject(FormBuilder);

  protected readonly busy = signal(false);
  protected readonly sent = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    kind: [this.route.snapshot.queryParamMap.get('tipo') === 'demo' ? 'demo' : 'contact'],
    name: ['', [Validators.required, Validators.maxLength(120)]],
    email: ['', [Validators.required, Validators.email, Validators.maxLength(256)]],
    phone: ['', [Validators.pattern(/^[+0-9][0-9 ()-]{6,28}$/)]],
    gymName: ['', [Validators.maxLength(200)]],
    message: [this.initialMessage(), [Validators.required, Validators.minLength(10), Validators.maxLength(1000)]],
    website: [''],
    acceptPrivacy: [false, [Validators.requiredTrue]],
  });

  constructor() {
    pageSeo('/contacto', 'site.contact.seoTitle', 'site.contact.seoDescription');
  }

  protected invalid(name: keyof typeof this.form.controls): boolean {
    const control = this.form.controls[name];
    return control.invalid && (control.touched || control.dirty);
  }

  protected send(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }
    const v = this.form.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.api
      .submitContact(v.kind === 'demo' ? 'demo' : 'contact', {
        name: v.name,
        email: v.email,
        phone: v.phone || null,
        gymName: v.gymName || null,
        message: v.message,
        acceptPrivacy: v.acceptPrivacy,
        privacyVersion: LEGAL_VERSION,
        website: v.website || null,
        locale: 'es',
      })
      .subscribe({
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

  /** Si viene de «hablar con ventas» sobre un plan, el mensaje ya menciona cuál. */
  private initialMessage(): string {
    const plan = this.route.snapshot.queryParamMap.get('plan');
    return this.route.snapshot.queryParamMap.get('tipo') === 'ventas' && plan ? `Quiero información sobre el plan ${plan.slice(0, 40)}.` : '';
  }
}

/** El registro en línea llega con el siguiente bloque de esta fase; mientras tanto se ofrece el camino real: la solicitud de demostración. */
@Component({
  selector: 'fc-public-register-soon',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        <h1>{{ 'site.register.title' | transloco }}</h1>
        <p class="pub-lead">{{ 'site.register.lead' | transloco }}</p>
        <div class="pub-cta">
          <a class="btn btn-primary btn-lg" routerLink="/contacto" [queryParams]="{ tipo: 'demo' }">{{ 'site.home.ctaDemo' | transloco }}</a>
          <a class="btn btn-lg" routerLink="/planes">{{ 'site.home.seePlans' | transloco }}</a>
        </div>
      </div>
    </section>
  `,
})
export class PublicRegisterSoon {
  constructor() {
    pageSeo('/registro', 'site.register.seoTitle', 'site.register.seoDescription');
  }
}
