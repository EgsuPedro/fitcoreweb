import { ChangeDetectionStrategy, Component, Injectable, inject, signal } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

interface Toast {
  id: number;
  text: string;
}

/**
 * Avisos de éxito. Solo se invocan desde el `next` de la respuesta del servidor: nunca antes de que el backend confirme
 * la operación (nada de "guardado" optimista).
 */
@Injectable({ providedIn: 'root' })
export class ToastService {
  private readonly transloco = inject(TranslocoService);
  private nextId = 1;

  readonly toasts = signal<Toast[]>([]);

  success(key: string, params: Record<string, unknown> = {}): void {
    const id = this.nextId++;
    this.toasts.update((list) => [...list, { id, text: this.transloco.translate(key, params) }]);
    setTimeout(() => this.dismiss(id), 5000);
  }

  dismiss(id: number): void {
    this.toasts.update((list) => list.filter((toast) => toast.id !== id));
  }
}

@Component({
  selector: 'fc-toasts',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="toasts" role="status" aria-live="polite">
      @for (toast of service.toasts(); track toast.id) {
        <div class="toast">
          <span>{{ toast.text }}</span>
          <button type="button" class="btn btn-ghost btn-icon" [attr.aria-label]="'common.toastClose' | transloco" (click)="service.dismiss(toast.id)">×</button>
        </div>
      }
    </div>
  `,
})
export class Toasts {
  protected readonly service = inject(ToastService);
}