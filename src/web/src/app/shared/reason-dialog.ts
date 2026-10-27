import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { Modal } from './modal';

/**
 * Confirmación de una operación sensible que exige un motivo (cancelar, anular, revertir…). El padre hace la llamada a la API
 * y muestra aquí el resultado: este diálogo no da nada por hecho hasta que el servidor responde.
 */
@Component({
  selector: 'fc-reason-dialog',
  imports: [Modal, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="title()" (closed)="cancelled.emit()">
      @if (message()) {
        <p>{{ message() }}</p>
      }
      @if (error()) {
        <p class="alert alert-danger" role="alert">{{ error() }}</p>
      }
      <label class="field">
        <span>{{ label() }}</span>
        <textarea [value]="reason()" maxlength="300" (input)="reason.set($any($event.target).value)"></textarea>
        @if (hint()) {
          <small class="muted">{{ hint() }}</small>
        }
      </label>
      <ng-container footer>
        <button type="button" class="btn" (click)="cancelled.emit()">{{ 'common.cancel' | transloco }}</button>
        <button type="button" class="btn" [class.btn-danger]="danger()" [class.btn-primary]="!danger()" [disabled]="busy() || !valid()" (click)="submitted.emit(reason().trim())">
          {{ confirmLabel() }}
        </button>
      </ng-container>
    </fc-modal>
  `,
})
export class ReasonDialog {
  readonly title = input.required<string>();
  readonly label = input.required<string>();
  readonly confirmLabel = input.required<string>();
  readonly message = input('');
  readonly hint = input('');
  readonly danger = input(false);
  /** Caracteres mínimos; 0 vuelve el motivo opcional. */
  readonly minLength = input(5);
  readonly busy = input(false);
  readonly error = input<string | null>(null);

  readonly submitted = output<string>();
  readonly cancelled = output<void>();

  protected readonly reason = signal('');

  protected valid(): boolean {
    return this.reason().trim().length >= this.minLength();
  }
}
