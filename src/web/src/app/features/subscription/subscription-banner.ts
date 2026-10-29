import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { FcDate } from '../../shared/format.pipes';
import { alertClass, sortBySeverity } from './subscription-math';
import { SubscriptionStatus } from './subscription-status';

/**
 * Banner comercial del gimnasio: prueba por terminar, pago próximo, mora con gracia, suspensión, vencimiento. Se calcula con el estado real de la
 * suscripción. Mientras el gimnasio está en regla no ocupa lugar. Con la suscripción restringida explica qué se puede y qué no.
 */
@Component({
  selector: 'fc-subscription-banner',
  imports: [RouterLink, TranslocoPipe, FcDate],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @for (a of alerts(); track a.code) {
      <div class="alert" [class]="cls(a.severity)" [attr.role]="a.severity === 'Critical' ? 'alert' : 'status'">
        <strong>{{ 'mySubscription.alert.' + a.code + '.title' | transloco }}</strong>
        {{ 'mySubscription.alert.' + a.code + '.body' | transloco: { date: a.date ? (a.date | fcDate) : '' } }}
        @if (!onPortal()) {
          <a routerLink="/my/subscription">{{ 'mySubscription.alert.view' | transloco }}</a>
        }
      </div>
    }
  `,
})
export class SubscriptionBanner implements OnInit {
  private readonly status = inject(SubscriptionStatus);
  private readonly router = inject(Router);

  protected readonly alerts = computed(() => sortBySeverity(this.status.current()?.alerts ?? []).filter((a) => a.code !== 'cancellation_scheduled'));

  ngOnInit(): void {
    this.status.refresh();
  }

  protected onPortal(): boolean {
    return this.router.url.startsWith('/my/subscription');
  }

  protected cls(severity: string): string {
    return alertClass(severity);
  }
}
