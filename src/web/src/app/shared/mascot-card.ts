import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/**
 * Tarjeta de bienvenida con la mascota «Fiti». Presentacional: recibe el saludo, el texto guía y la etiqueta de la acción principal ya traducidos
 * y avisa con `action` cuando se pulsa el botón. La mascota es una animación (GIF) y, para quien pide menos movimiento, una imagen fija.
 * Cuando exista una versión más liviana (MP4/WebM o Lottie) se cambia aquí sin tocar quien usa la tarjeta.
 */
@Component({
  selector: 'fc-mascot-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="mascot-card" [attr.aria-label]="title()">
      <div>
        <h2 class="mascot-title">{{ title() }}</h2>
        <p class="mascot-text">{{ text() }}</p>
        @if (actionLabel()) {
          <button type="button" class="btn btn-primary" (click)="action.emit()">{{ actionLabel() }}</button>
        }
      </div>
      <!-- Animación de Fiti (GIF); con «reducir movimiento» o en pantallas angostas (celular: no se descargan 7 MB por datos móviles) se muestra la imagen fija. -->
      <picture>
        <source media="(prefers-reduced-motion: reduce), (max-width: 80rem)" srcset="brand/fiti.png" />
        <img class="mascot-figure animated" src="brand/fiti-animado.gif" alt="" width="720" height="1280" loading="lazy" decoding="async" />
      </picture>
    </section>
  `,
})
export class MascotCard {
  readonly title = input.required<string>();
  readonly text = input.required<string>();
  readonly actionLabel = input<string | null>(null);
  readonly action = output<void>();
}
