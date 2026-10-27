import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { SessionService } from '../core/auth/session.service';
import { Lang, SUPPORTED_LANGS, setLang } from '../core/i18n/transloco';
import { NAV_ITEMS, visibleNavItems } from './nav';

@Component({
  selector: 'fc-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="shell" [class.nav-open]="navOpen()">
      <aside class="sidebar">
        <div class="sidebar-brand">
          <span class="brand-mark" aria-hidden="true">F</span>
          <span>FitCore 360</span>
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
          <span class="topbar-spacer"></span>

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
          <button type="button" class="btn" (click)="logout()">{{ 'nav.logout' | transloco }}</button>
        </header>

        <main class="content" id="content">
          <router-outlet />
        </main>
      </div>
    </div>
  `,
})
export class Shell {
  protected readonly session = inject(SessionService);
  private readonly router = inject(Router);
  private readonly transloco = inject(TranslocoService);

  protected readonly langs = SUPPORTED_LANGS;
  protected readonly navOpen = signal(false);
  protected readonly activeLang = signal<string>(this.transloco.getActiveLang());
  protected readonly items = computed(() => visibleNavItems(NAV_ITEMS, (permission) => this.session.has(permission)));

  protected changeLang(lang: Lang): void {
    setLang(this.transloco, lang);
    this.activeLang.set(lang);
  }

  protected logout(): void {
    this.session.logout().subscribe(() => void this.router.navigate(['/login']));
  }
}
