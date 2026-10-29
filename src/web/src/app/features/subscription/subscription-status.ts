import { Injectable, inject, signal } from '@angular/core';
import { ApiService } from '../../core/api/api.service';
import type { MySubscription } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';

/**
 * Situación comercial del gimnasio para el banner y el portal. Una sola copia compartida: el banner y «Mi suscripción» ven siempre lo mismo y,
 * cuando el propietario pide algo o marca avisos, se refresca para que ambos se actualicen. Quien no tiene permiso no la consulta.
 */
@Injectable({ providedIn: 'root' })
export class SubscriptionStatus {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);

  readonly current = signal<MySubscription | null>(null);

  refresh(): void {
    if (!this.session.has(Perm.SubscriptionRead)) {
      this.current.set(null);
      return;
    }

    // Si falla (sin red, sin suscripción) no se muestra banner: nunca se inventa una situación.
    this.api.mySubscription().subscribe({ next: (s) => this.current.set(s), error: () => this.current.set(null) });
  }
}
