import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { SessionService } from '../../core/auth/session.service';
import { NAV_ITEMS, visibleNavItems } from '../../layout/nav';
import { Perm } from '../../core/auth/permissions';
import { MyPortal } from '../portal/my-portal';
import { MascotCard } from '../../shared/mascot-card';

@Component({
  selector: 'fc-home',
  imports: [RouterLink, TranslocoPipe, MyPortal, MascotCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!isStudent()) {
      <header class="page-header">
        <h1>{{ 'home.welcome' | transloco: { name: session.user()?.fullName } }}</h1>
        <p class="muted">{{ 'home.tenant' | transloco: { tenant: session.user()?.tenantSlug } }}</p>
      </header>

      <fc-mascot-card
        [title]="'home.mascot.title' | transloco: { name: firstName() }"
        [text]="'home.mascot.text' | transloco"
        [actionLabel]="mascotLink() ? ('home.mascot.action' | transloco) : null"
        (action)="goToMascotLink()"
      />
    }

    @if (isStudent()) {
      <fc-my-portal />
    } @else {
    <section class="card">
      <h2>{{ 'home.access' | transloco }}</h2>
      <dl class="summary">
        <dt>{{ 'home.roles' | transloco }}</dt>
        <dd>
          @for (role of session.user()?.roles; track role) {
            <span class="badge">{{ role }}</span>
          } @empty {
            <span class="muted">—</span>
          }
        </dd>
        <dt>{{ 'home.branches' | transloco }}</dt>
        <dd>
          @if (session.allBranches()) {
            {{ 'home.allBranches' | transloco }}
          } @else {
            {{ session.user()?.branchIds?.length ?? 0 }}
          }
        </dd>
        <dt>{{ 'home.permissions' | transloco }}</dt>
        <dd>{{ session.permissions().size }}</dd>
      </dl>
    </section>

    @if (links().length > 0) {
      <section class="card">
        <h2>{{ 'home.shortcuts' | transloco }}</h2>
        <div class="shortcuts">
          @for (item of links(); track item.path) {
            <a class="btn" [routerLink]="item.path">{{ item.labelKey | transloco }}</a>
          }
        </div>
      </section>
    } @else {
      <p class="alert alert-warning">{{ 'home.noAccess' | transloco }}</p>
    }
    }
  `,
})
export class Home {
  protected readonly session = inject(SessionService);
  private readonly router = inject(Router);
  protected readonly isStudent = computed(() => this.session.has(Perm.MeRead) && this.links().length === 0);
  protected readonly firstName = computed(() => (this.session.user()?.fullName ?? '').trim().split(/\s+/)[0] ?? '');

  /** La acción rápida lleva al primer lugar útil del menú (métricas o analítica si las ve; si no, lo primero que tenga). */
  protected readonly mascotLink = computed(() => {
    const preferred = ['/platform/metrics', '/analytics', '/members', '/platform/organizations'];
    const paths = this.links().map((l) => l.path);
    return preferred.find((p) => paths.includes(p)) ?? paths[0] ?? null;
  });

  protected goToMascotLink(): void {
    const target = this.mascotLink();
    if (target) {
      void this.router.navigateByUrl(target);
    }
  }

  protected readonly links = computed(() =>
    visibleNavItems(NAV_ITEMS, (permission) => this.session.has(permission)).filter((item) => item.path !== '/'),
  );
}
