import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Invitation, MySubscription, Onboarding } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { Badge } from '../../shared/badge';
import { FcDate } from '../../shared/format.pipes';

/**
 * Resumen del gimnasio para quien lo administra: cómo va la configuración inicial, qué plan tiene y a quién ha invitado. Cada tarjeta se muestra solo si la persona tiene el
 * permiso de ver ese dato; si una consulta falla, la tarjeta se omite en lugar de romper el inicio.
 */
@Component({
  selector: 'fc-owner-panel',
  imports: [RouterLink, TranslocoPipe, Badge, FcDate],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible()) {
      <section class="owner-panel" [attr.aria-label]="'home.owner.title' | transloco">
        <h2>{{ 'home.owner.title' | transloco }}</h2>
        <div class="owner-grid">
          @if (setup(); as s) {
            <article class="card owner-tile">
              <h3>{{ 'home.owner.setup' | transloco }}</h3>
              @if (s.completed) {
                <p><fc-badge tone="ok">{{ 'home.owner.setupDone' | transloco }}</fc-badge></p>
                <a routerLink="/onboarding">{{ 'home.owner.setupEdit' | transloco }}</a>
              } @else {
                <p>{{ 'home.owner.setupProgress' | transloco: { done: requiredDone(), total: 2 } }}</p>
                <progress max="2" [value]="requiredDone()" [attr.aria-label]="'home.owner.setup' | transloco"></progress>
                <a class="btn btn-primary" routerLink="/onboarding">{{ 'onboarding.bannerAction' | transloco }}</a>
              }
            </article>
          }
          @if (plan(); as p) {
            <article class="card owner-tile">
              <h3>{{ 'home.owner.plan' | transloco }}</h3>
              <p><strong>{{ p.planName }}</strong> <fc-badge [tone]="planTone()">{{ 'platform.status.' + p.status | transloco }}</fc-badge></p>
              @if (p.trialEndsOn) {
                <p class="muted">{{ 'home.owner.trialUntil' | transloco: { date: (p.trialEndsOn | fcDate) } }}</p>
              } @else {
                <p class="muted">{{ 'home.owner.nextDue' | transloco: { date: (p.nextDueOn | fcDate) } }}</p>
              }
              <a routerLink="/my/subscription">{{ 'home.owner.planView' | transloco }}</a>
            </article>
          }
          @if (invitations(); as list) {
            <article class="card owner-tile">
              <h3>{{ 'home.owner.team' | transloco }}</h3>
              <p>{{ 'home.owner.pending' | transloco: { count: pending() } }}</p>
              @if (canInvite()) { <a class="btn" routerLink="/invitations">{{ 'invitations.new' | transloco }}</a> }
              @if (list.length === 0 && canInvite()) { <p class="muted">{{ 'home.owner.noInvitations' | transloco }}</p> }
            </article>
          }
        </div>
      </section>
    }
  `,
})
export class OwnerPanel implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);

  protected readonly setup = signal<Onboarding | null>(null);
  protected readonly plan = signal<MySubscription | null>(null);
  protected readonly invitations = signal<Invitation[] | null>(null);

  protected readonly visible = computed(() => this.session.has(Perm.OrganizationUpdate));
  protected readonly canInvite = computed(() => this.session.has(Perm.UsersCreate));
  protected readonly requiredDone = computed(() => this.setup()?.steps.filter((s) => s.required && s.status === 'Done').length ?? 0);
  protected readonly pending = computed(() => this.invitations()?.filter((i) => i.status === 'Pending').length ?? 0);
  protected readonly planTone = computed(() => {
    const status = this.plan()?.status;
    return status === 'Active' || status === 'Trialing' ? 'ok' : status === 'PastDue' ? 'warn' : 'off';
  });

  ngOnInit(): void {
    if (!this.visible()) {
      return;
    }
    this.api.onboarding().subscribe({ next: (s) => this.setup.set(s), error: () => undefined });
    if (this.session.has(Perm.SubscriptionRead)) {
      this.api.mySubscription().subscribe({ next: (p) => this.plan.set(p), error: () => undefined });
    }
    if (this.session.has(Perm.UsersRead)) {
      this.api.invitations().subscribe({ next: (l) => this.invitations.set(l), error: () => undefined });
    }
  }
}
