import { Injectable, effect, inject, signal } from '@angular/core';
import { ApiService } from '../../core/api/api.service';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';

/**
 * Avisos sin leer del usuario de la sesión. Se consulta al abrir la sesión y luego cada minuto; se pone a cero al cerrarla,
 * para que el contador de un usuario nunca se vea en la sesión de otro.
 */
@Injectable({ providedIn: 'root' })
export class InboxService {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);

  readonly unread = signal(0);

  constructor() {
    effect((onCleanup) => {
      const user = this.session.user();
      this.unread.set(0);
      if (!user || !this.session.has(Perm.InboxUse)) {
        return;
      }

      this.refresh();
      const timer = setInterval(() => this.refresh(), 60_000);
      onCleanup(() => clearInterval(timer));
    });
  }

  refresh(): void {
    this.api.inboxSummary().subscribe({
      next: (summary) => this.unread.set(summary.unread),
      error: () => {
        // Un fallo de red no debe molestar: el contador se actualiza en la próxima consulta.
      },
    });
  }
}
