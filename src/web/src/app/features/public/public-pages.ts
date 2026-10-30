import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { pageSeo } from '../../core/page-seo';
import { LEGAL_VERSION, PRIVACY_SECTIONS, TERMS_SECTIONS } from './legal-texts';

/** Identificadores de los textos del sitio: cada uno tiene su título y su texto en `site.*` (es/en). */
export const HOME_CARDS = ['members', 'billing', 'attendance', 'classes', 'wellness', 'reports'] as const;
export const FEATURE_GROUPS: { id: string; items: string[] }[] = [
  { id: 'members', items: ['profiles', 'memberships', 'portal'] },
  { id: 'billing', items: ['payments', 'receivables', 'cash', 'promotions'] },
  { id: 'attendance', items: ['checkin', 'debtRule', 'history'] },
  { id: 'classes', items: ['schedule', 'reservations', 'waitlist', 'notices'] },
  { id: 'wellness', items: ['routines', 'nutrition', 'assessments', 'photos'] },
  { id: 'reports', items: ['dashboard', 'financial', 'excel', 'branches'] },
  { id: 'team', items: ['roles', 'branches', 'invitations', 'audit'] },
  { id: 'security', items: ['isolation', 'consent', 'sessions'] },
];
export const SOON_ITEMS = ['sri', 'payroll', 'mobile', 'whatsapp', 'payments'] as const;
export const FAQ_ITEMS = ['what', 'price', 'trial', 'data', 'health', 'sri', 'mobile', 'support', 'languages'] as const;
export const STEPS = ['register', 'confirm', 'setup'] as const;

/** Inicio del sitio comercial. Solo describe funciones que existen; lo demás va marcado «próximamente» en Funcionalidades. */
@Component({
  selector: 'fc-public-home',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-hero">
      <div class="pub-wrap pub-hero-grid">
        <div>
          <h1>{{ 'site.home.title' | transloco }}</h1>
          <p class="pub-lead">{{ 'site.home.lead' | transloco }}</p>
          <div class="pub-cta">
            <a class="btn btn-primary btn-lg" routerLink="/registro">{{ 'site.home.ctaRegister' | transloco }}</a>
            <a class="btn btn-lg" routerLink="/contacto" [queryParams]="{ tipo: 'demo' }">{{ 'site.home.ctaDemo' | transloco }}</a>
          </div>
          <p class="muted">{{ 'site.home.note' | transloco }}</p>
        </div>
        <img class="pub-hero-art" src="brand/fiti-hero.webp" alt="" width="360" height="468" decoding="async" />
      </div>
    </section>

    <section class="pub-section">
      <div class="pub-wrap">
        <h2>{{ 'site.home.cardsTitle' | transloco }}</h2>
        <ul class="pub-cards">
          @for (id of cards; track id) {
            <li class="pub-card">
              <h3>{{ 'site.home.cards.' + id + '.title' | transloco }}</h3>
              <p>{{ 'site.home.cards.' + id + '.text' | transloco }}</p>
            </li>
          }
        </ul>
        <p><a routerLink="/funcionalidades">{{ 'site.home.allFeatures' | transloco }}</a></p>
      </div>
    </section>

    <section class="pub-section pub-alt">
      <div class="pub-wrap">
        <h2>{{ 'site.home.stepsTitle' | transloco }}</h2>
        <ol class="pub-steps">
          @for (id of steps; track id; let i = $index) {
            <li>
              <span class="pub-step-num" aria-hidden="true">{{ i + 1 }}</span>
              <div>
                <h3>{{ 'site.home.steps.' + id + '.title' | transloco }}</h3>
                <p>{{ 'site.home.steps.' + id + '.text' | transloco }}</p>
              </div>
            </li>
          }
        </ol>
      </div>
    </section>

    <section class="pub-section">
      <div class="pub-wrap pub-final">
        <h2>{{ 'site.home.finalTitle' | transloco }}</h2>
        <p>{{ 'site.home.finalText' | transloco }}</p>
        <div class="pub-cta">
          <a class="btn btn-primary btn-lg" routerLink="/planes">{{ 'site.home.seePlans' | transloco }}</a>
          <a class="btn btn-lg" routerLink="/contacto">{{ 'site.home.talk' | transloco }}</a>
        </div>
      </div>
    </section>
  `,
})
export class PublicHome {
  protected readonly cards = HOME_CARDS;
  protected readonly steps = STEPS;

  constructor() {
    pageSeo('/', 'site.home.seoTitle', 'site.home.seoDescription', () => ({
      '@context': 'https://schema.org',
      '@type': 'SoftwareApplication',
      name: 'FIT360',
      applicationCategory: 'BusinessApplication',
      operatingSystem: 'Web',
      inLanguage: ['es', 'en'],
    }));
  }
}

/** Funcionalidades reales, por tema, y lo que viene (marcado como tal, sin fechas inventadas). */
@Component({
  selector: 'fc-public-features',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap">
        <h1>{{ 'site.features.title' | transloco }}</h1>
        <p class="pub-lead">{{ 'site.features.lead' | transloco }}</p>
        @for (group of groups; track group.id) {
          <article class="pub-group">
            <h2>{{ 'site.features.' + group.id + '.title' | transloco }}</h2>
            <ul>
              @for (item of group.items; track item) {
                <li>{{ 'site.features.' + group.id + '.items.' + item | transloco }}</li>
              }
            </ul>
          </article>
        }
        <article class="pub-group pub-soon">
          <h2>{{ 'site.features.soonTitle' | transloco }}</h2>
          <p class="muted">{{ 'site.features.soonLead' | transloco }}</p>
          <ul>
            @for (item of soon; track item) {
              <li>{{ 'site.features.soon.' + item | transloco }} <span class="badge">{{ 'site.features.soonBadge' | transloco }}</span></li>
            }
          </ul>
        </article>
        <div class="pub-cta">
          <a class="btn btn-primary btn-lg" routerLink="/registro">{{ 'site.home.ctaRegister' | transloco }}</a>
          <a class="btn btn-lg" routerLink="/contacto" [queryParams]="{ tipo: 'demo' }">{{ 'site.home.ctaDemo' | transloco }}</a>
        </div>
      </div>
    </section>
  `,
})
export class PublicFeatures {
  protected readonly groups = FEATURE_GROUPS;
  protected readonly soon = SOON_ITEMS;

  constructor() {
    pageSeo('/funcionalidades', 'site.features.seoTitle', 'site.features.seoDescription');
  }
}

@Component({
  selector: 'fc-public-about',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        <h1>{{ 'site.about.title' | transloco }}</h1>
        <p class="pub-lead">{{ 'site.about.lead' | transloco }}</p>
        <h2>{{ 'site.about.whatTitle' | transloco }}</h2>
        <p>{{ 'site.about.what' | transloco }}</p>
        <h2>{{ 'site.about.principlesTitle' | transloco }}</h2>
        <ul>
          <li>{{ 'site.about.p1' | transloco }}</li>
          <li>{{ 'site.about.p2' | transloco }}</li>
          <li>{{ 'site.about.p3' | transloco }}</li>
        </ul>
        <p><a class="btn btn-primary" routerLink="/contacto">{{ 'site.home.talk' | transloco }}</a></p>
      </div>
    </section>
  `,
})
export class PublicAbout {
  constructor() {
    pageSeo('/nosotros', 'site.about.seoTitle', 'site.about.seoDescription');
  }
}

/** Preguntas frecuentes (con datos estructurados FAQPage para buscadores). */
@Component({
  selector: 'fc-public-faq',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        <h1>{{ 'site.faq.title' | transloco }}</h1>
        <p class="pub-lead">{{ 'site.faq.lead' | transloco }}</p>
        @for (id of items; track id) {
          <details class="pub-faq">
            <summary>{{ 'site.faq.' + id + '.q' | transloco }}</summary>
            <p>{{ 'site.faq.' + id + '.a' | transloco }}</p>
          </details>
        }
        <p>{{ 'site.faq.more' | transloco }} <a routerLink="/contacto">{{ 'site.nav.contact' | transloco }}</a></p>
      </div>
    </section>
  `,
})
export class PublicFaq {
  protected readonly items = FAQ_ITEMS;

  constructor() {
    pageSeo('/preguntas-frecuentes', 'site.faq.seoTitle', 'site.faq.seoDescription', (t: TranslocoService) => ({
      '@context': 'https://schema.org',
      '@type': 'FAQPage',
      mainEntity: FAQ_ITEMS.map((id) => ({
        '@type': 'Question',
        name: t.translate(`site.faq.${id}.q`),
        acceptedAnswer: { '@type': 'Answer', text: t.translate(`site.faq.${id}.a`) },
      })),
    }));
  }
}

/** Texto legal (términos o privacidad) con su versión y la advertencia de que es un borrador pendiente de revisión legal. */
@Component({
  selector: 'fc-public-legal',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="pub-section">
      <div class="pub-wrap pub-narrow">
        <h1>{{ (privacy ? 'site.legal.privacyTitle' : 'site.legal.termsTitle') | transloco }}</h1>
        <p class="alert alert-warning" role="note">{{ 'site.legal.draft' | transloco }}</p>
        <p class="muted">{{ 'site.legal.version' | transloco: { version: version } }}</p>
        @for (section of sections; track section.heading) {
          <h2>{{ section.heading }}</h2>
          @for (p of section.paragraphs; track p) { <p>{{ p }}</p> }
        }
        <p><a routerLink="/contacto">{{ 'site.nav.contact' | transloco }}</a></p>
      </div>
    </section>
  `,
})
export class PublicLegal {
  protected readonly privacy = inject(ActivatedRoute).snapshot.data['privacy'] === true;
  protected readonly sections = this.privacy ? PRIVACY_SECTIONS : TERMS_SECTIONS;
  protected readonly version = LEGAL_VERSION;

  constructor() {
    pageSeo(this.privacy ? '/privacidad' : '/terminos', this.privacy ? 'site.legal.privacyTitle' : 'site.legal.termsTitle', 'site.legal.seoDescription');
  }
}
