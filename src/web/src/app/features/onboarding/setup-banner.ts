import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';

/** Aviso fijo mientras el gimnasio tiene la configuración inicial pendiente. No bloquea nada: solo lleva al asistente a quien puede completarlo. */
@Component({
  selector: 'fc-setup-banner',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible()) {
      <p class="alert alert-info setup-banner" role="status">
        <span>{{ 'onboarding.banner' | transloco }}</span>
        <a class="btn btn-primary" routerLink="/onboarding">{{ 'onboarding.bannerAction' | transloco }}</a>
      </p>
    }
  `,
})
export class SetupBanner {
  private readonly session = inject(SessionService);
  protected readonly visible = computed(() => this.session.user()?.setupStatus === 'PendingSetup' && this.session.has(Perm.OrganizationUpdate));
}
