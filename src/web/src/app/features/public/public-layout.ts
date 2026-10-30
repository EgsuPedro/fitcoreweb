import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Lang, SUPPORTED_LANGS, setLang } from '../../core/i18n/transloco';

/**
 * Marco de las páginas públicas del sitio comercial: cabecera con navegación y acciones, contenido y pie. Es accesible por teclado (enlace «saltar al contenido», menú
 * con estado anunciado) y se adapta al celular (menú plegable).
 */
@Component({
  selector: 'fc-public-layout',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="skip-link" href="#contenido">{{ 'site.skip' | transloco }}</a>
    <header class="pub-header">
      <div class="pub-wrap pub-bar">
        <a class="pub-brand" routerLink="/" [attr.aria-label]="'site.brandHome' | transloco">
          <img src="brand/logo-header.webp" alt="FIT360" width="44" height="38" />
        </a>
        <button type="button" class="btn pub-menu-toggle" [attr.aria-expanded]="open()" aria-controls="pub-nav" (click)="open.set(!open())">
          {{ (open() ? 'site.menuClose' : 'site.menuOpen') | transloco }}
        </button>
        <nav id="pub-nav" class="pub-nav" [class.open]="open()" [attr.aria-label]="'site.navLabel' | transloco" (click)="open.set(false)">
          <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">{{ 'site.nav.home' | transloco }}</a>
          <a routerLink="/funcionalidades" routerLinkActive="active">{{ 'site.nav.features' | transloco }}</a>
          <a routerLink="/planes" routerLinkActive="active">{{ 'site.nav.plans' | transloco }}</a>
          <a routerLink="/nosotros" routerLinkActive="active">{{ 'site.nav.about' | transloco }}</a>
          <a routerLink="/preguntas-frecuentes" routerLinkActive="active">{{ 'site.nav.faq' | transloco }}</a>
          <a routerLink="/contacto" routerLinkActive="active">{{ 'site.nav.contact' | transloco }}</a>
        </nav>
        <div class="pub-actions">
          <span class="lang-switch">
            @for (lang of langs; track lang) {
              <button type="button" class="lang-btn" [class.active]="transloco.getActiveLang() === lang" [attr.aria-pressed]="transloco.getActiveLang() === lang" (click)="switch(lang)">{{ lang.toUpperCase() }}</button>
            }
          </span>
          <a class="btn" routerLink="/login">{{ 'site.nav.login' | transloco }}</a>
          <a class="btn btn-primary" routerLink="/registro">{{ 'site.nav.register' | transloco }}</a>
        </div>
      </div>
    </header>

    <main id="contenido" tabindex="-1">
      <router-outlet />
    </main>

    <footer class="pub-footer">
      <div class="pub-wrap pub-footer-grid">
        <div>
          <img src="brand/logo-header.webp" alt="FIT360" width="44" height="38" loading="lazy" />
          <p class="muted">{{ 'site.footer.tagline' | transloco }}</p>
        </div>
        <nav [attr.aria-label]="'site.footer.explore' | transloco">
          <h2>{{ 'site.footer.explore' | transloco }}</h2>
          <a routerLink="/funcionalidades">{{ 'site.nav.features' | transloco }}</a>
          <a routerLink="/planes">{{ 'site.nav.plans' | transloco }}</a>
          <a routerLink="/preguntas-frecuentes">{{ 'site.nav.faq' | transloco }}</a>
          <a routerLink="/contacto">{{ 'site.nav.contact' | transloco }}</a>
        </nav>
        <nav [attr.aria-label]="'site.footer.legal' | transloco">
          <h2>{{ 'site.footer.legal' | transloco }}</h2>
          <a routerLink="/terminos">{{ 'site.footer.terms' | transloco }}</a>
          <a routerLink="/privacidad">{{ 'site.footer.privacy' | transloco }}</a>
          <a routerLink="/login">{{ 'site.nav.login' | transloco }}</a>
        </nav>
      </div>
      <p class="pub-wrap muted pub-copy">{{ 'site.footer.copy' | transloco }}</p>
    </footer>
  `,
})
export class PublicLayout {
  protected readonly transloco = inject(TranslocoService);
  private readonly router = inject(Router);
  protected readonly langs = SUPPORTED_LANGS;
  protected readonly open = signal(false);

  protected switch(lang: Lang): void {
    setLang(this.transloco, lang);
  }

  constructor() {
    // Al navegar a otra página se cierra el menú plegable del celular.
    this.router.events.subscribe(() => this.open.set(false));
  }
}
