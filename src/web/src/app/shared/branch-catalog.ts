import { Injectable, effect, inject, signal } from '@angular/core';
import { ApiService } from '../core/api/api.service';
import type { Branch } from '../core/api/models';
import { SessionService } from '../core/auth/session.service';

/**
 * Sucursales a las que el usuario tiene acceso, cargadas una vez para los selectores. Se vacía al cambiar la sesión:
 * jamás debe sobrevivir al cierre de sesión mostrando sucursales de otro gimnasio.
 */
@Injectable({ providedIn: 'root' })
export class BranchCatalog {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private loaded = false;

  readonly branches = signal<Branch[]>([]);

  constructor() {
    effect(() => {
      this.session.user();
      this.loaded = false;
      this.branches.set([]);
    });
  }

  ensure(): void {
    if (this.loaded) {
      return;
    }
    this.loaded = true;
    this.api.branches({ pageSize: 100, isActive: true }).subscribe({
      next: (page) => this.branches.set(page.items),
      error: () => {
        this.loaded = false;
      },
    });
  }

  nameOf(id: string): string {
    return this.branches().find((branch) => branch.id === id)?.name ?? '';
  }
}