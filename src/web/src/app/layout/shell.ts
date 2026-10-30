import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { BrandingService } from '../core/branding.service';
import { SessionService } from '../core/auth/session.service';
import { Lang, SUPPORTED_LANGS, setLang } from '../core/i18n/transloco';
import { InboxBell } from '../features/inbox/inbox-bell';
import { Seo } from '../core/seo.service';
import { SetupBanner } from '../features/onboarding/setup-banner';
import { SubscriptionBanner } from '../features/subscription/subscription-banner';
import { Toasts } from '../shared/toast';
import { NAV_ITEMS, visibleNavItems } from './nav';

@Component({
  selector: 'fc-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslocoPipe, Toasts, InboxBell, SubscriptionBanner, SetupBanner],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell" [class.nav-open]="navOpen()">
      <aside class="sidebar">
        <div class="sidebar-brand">
          <img class="brand-logo" src="brand/logo.png" alt="" width="381" height="328" />
          <span class="sr-only">FIT360</span>
        </div>
        <nav [attr.aria-label]="'nav.label' | transloco">
          @for (item of items(); track item.path) {
            <a
              class="nav-link"
              [routerLink]="item.path"
              routerLinkActive="active"
              [routerLinkActiveOptions]="{ exact: item.path === '/' }"
              (click)="navOpen.set(false)"
            >
              <span class="nav-icon" aria-hidden="true">{{ item.icon }}</span>
              {{ item.labelKey | transloco }}
            </a>
          }
        </nav>

        <!-- En celular la cuenta vive aquí (la barra superior no tiene espacio para tantos botones). -->
        <div class="sidebar-account">
          <p class="sidebar-user">
            <strong>{{ session.user()?.fullName }}</strong>
            <span>{{ session.user()?.tenantSlug }}</span>
          </p>
          <a class="nav-link" routerLink="/change-password" (click)="navOpen.set(false)">{{ 'account.changePasswordShort' | transloco }}</a>
          <button type="button" class="nav-link nav-button" (click)="logout()">{{ 'nav.logout' | transloco }}</button>
        </div>
      </aside>

      @if (navOpen()) {
        <button type="button" class="scrim" [attr.aria-label]="'common.close' | transloco" (click)="navOpen.set(false)"></button>
      }

      <div class="main">
        <header class="topbar">
          <button
            type="button"
            class="btn btn-ghost menu-button"
            [attr.aria-label]="'nav.toggle' | transloco"
            [attr.aria-expanded]="navOpen()"
            (click)="navOpen.set(!navOpen())"
          >
            ☰
          </button>
          @if (brand.logoUrl(); as logo) {
            <img class="tenant-logo" [src]="logo" [alt]="brand.branding()?.tenantName ?? ''" />
          } @else {
            <img class="topbar-brand" src="brand/logo.png" alt="" width="381" height="328" />
          }
          <span class="topbar-spacer"></span>

          <fc-inbox-bell />

          <span class="lang-switch">
            @for (lang of langs; track lang) {
              <button type="button" class="btn btn-ghost" [class.active]="activeLang() === lang" (click)="changeLang(lang)">
                {{ lang.toUpperCase() }}
              </button>
            }
          </span>

          <span class="user-chip">
            <span class="user-name">{{ session.user()?.fullName }}</span>
            <span class="muted user-tenant">{{ session.user()?.tenantSlug }}</span>
          </span>
          <a class="btn btn-ghost topbar-account" routerLink="/change-password">{{ 'account.changePasswordShort' | transloco }}</a>
          <button type="button" class="btn topbar-account" (click)="logout()">{{ 'nav.logout' | transloco }}</button>
        </header>

        <main class="content" id="content">
          <fc-setup-banner />
          <fc-subscription-banner />
          <router-outlet />
        </main>
        <fc-toasts />
      </div>
    </div>
  `,
})
export class Shell implements OnInit {
  private readonly seo = inject(Seo);

  protected readonly session = inject(SessionService);
  protected readonly brand = inject(BrandingService);
  private readonly router = inject(Router);
  private readonly transloco = inject(TranslocoService);

  protected readonly langs = SUPPORTED_LANGS;
  protected readonly navOpen = signal(false);
  protected readonly activeLang = signal<string>(this.transloco.getActiveLang());
  protected readonly items = computed(() => visibleNavItems(NAV_ITEMS, (permission) => this.session.has(permission)));

  ngOnInit(): void {
    // El panel es privado: los buscadores no lo indexan.
    this.seo.noIndex();
    this.brand.refresh();
  }

  protected changeLang(lang: Lang): void {
    setLang(this.transloco, lang);
    this.activeLang.set(lang);
  }

  protected logout(): void {
    this.session.logout().subscribe(() => {
      this.brand.clear();
      void this.router.navigate(['/login']);
    });
  }
}
