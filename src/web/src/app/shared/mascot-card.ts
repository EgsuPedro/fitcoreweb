import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

/**
 * Tarjeta de bienvenida con la mascota «Fiti». Presentacional: recibe el saludo, el texto guía y la etiqueta de la acción principal ya traducidos
 * y avisa con `action` cuando se pulsa el botón. La imagen es estática y liviana; el espacio queda listo para sustituirla por una animación
 * (Lottie o GIF) cuando exista, sin tocar quien usa la tarjeta.
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
      <img class="mascot-figure" src="brand/fiti.png" alt="" width="212" height="275" loading="lazy" decoding="async" />
    </section>
  `,
})
export class MascotCard {
  readonly title = input.required<string>();
  readonly text = input.required<string>();
  readonly actionLabel = input<string | null>(null);
  readonly action = output<void>();
}
