import { ChangeDetectionStrategy, Component, OnDestroy, effect, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../core/api/api.service';

/**
 * Foto privada. El navegador no manda el token en una etiqueta <img>, así que se pide con la sesión como blob y se muestra desde
 * una URL local temporal que se libera al cambiar de foto o destruir el componente (nunca queda una URL pública).
 */
@Component({
  selector: 'fc-auth-image',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (src(); as url) {
      <img [src]="url" [alt]="alt()" />
    } @else if (failed()) {
      <span class="muted">{{ 'photos.loadFailed' | transloco }}</span>
    } @else {
      <span class="muted">{{ 'common.loading' | transloco }}</span>
    }
  `,
})
export class AuthImage implements OnDestroy {
  private readonly api = inject(ApiService);

  readonly photoId = input.required<string>();
  readonly alt = input.required<string>();

  protected readonly src = signal<string | null>(null);
  protected readonly failed = signal(false);

  constructor() {
    effect((onCleanup) => {
      const subscription = this.api.progressPhotoContent(this.photoId()).subscribe({
        next: (blob) => {
          this.release();
          this.src.set(URL.createObjectURL(blob));
          this.failed.set(false);
        },
        error: () => this.failed.set(true),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  ngOnDestroy(): void {
    this.release();
  }

  private release(): void {
    const current = this.src();
    if (current) {
      URL.revokeObjectURL(current);
      this.src.set(null);
    }
  }
}
