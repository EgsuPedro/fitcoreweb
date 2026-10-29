import { Injectable, inject, signal } from '@angular/core';
import { ApiService } from './api/api.service';
import { Perm } from './auth/permissions';
import { SessionService } from './auth/session.service';
import type { Branding } from './api/models';

/**
 * Marca del gimnasio de la sesión (nombre y logo). Una sola copia compartida: el menú y la pantalla «Logo del gimnasio» ven siempre lo mismo y, al cambiar
 * el logo, se refresca. El logo se pide con la sesión como blob y se muestra desde una URL local temporal que se libera al cambiar.
 */
@Injectable({ providedIn: 'root' })
export class BrandingService {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);

  readonly branding = signal<Branding | null>(null);
  readonly logoUrl = signal<string | null>(null);

  refresh(): void {
    // Ver la marca exige el permiso de ver los datos del gimnasio; quien no lo tiene (p. ej. un alumno) simplemente no la ve.
    if (!this.session.has(Perm.OrganizationRead)) {
      this.clear();
      return;
    }

    this.api.branding().subscribe({
      next: (b) => {
        this.branding.set(b);
        if (!b.hasLogo) {
          this.setUrl(null);
          return;
        }

        this.api.brandingLogo().subscribe({ next: (blob) => this.setUrl(URL.createObjectURL(blob)), error: () => this.setUrl(null) });
      },
      error: () => {
        this.branding.set(null);
        this.setUrl(null);
      },
    });
  }

  /** Al cerrar la sesión no debe quedar la marca de un gimnasio para el siguiente usuario del navegador. */
  clear(): void {
    this.branding.set(null);
    this.setUrl(null);
  }

  private setUrl(url: string | null): void {
    const previous = this.logoUrl();
    if (previous) {
      URL.revokeObjectURL(previous);
    }

    this.logoUrl.set(url);
  }
}
