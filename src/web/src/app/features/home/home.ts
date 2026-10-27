import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { SessionService } from '../../core/auth/session.service';
import { NAV_ITEMS, visibleNavItems } from '../../layout/nav';
import { Perm } from '../../core/auth/permissions';
import { MyPortal } from '../portal/my-portal';

@Component({
  selector: 'fc-home',
  imports: [RouterLink, TranslocoPipe, MyPortal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'home.welcome' | transloco: { name: session.user()?.fullName } }}</h1>
      <p class="muted">{{ 'home.tenant' | transloco: { tenant: session.user()?.tenantSlug } }}</p>
    </header>

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
  protected readonly isStudent = computed(() => this.session.has(Perm.MeRead) && this.links().length === 0);
  protected readonly links = computed(() =>
    visibleNavItems(NAV_ITEMS, (permission) => this.session.has(permission)).filter((item) => item.path !== '/'),
  );
}
