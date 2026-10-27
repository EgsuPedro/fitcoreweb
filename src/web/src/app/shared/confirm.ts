import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { Modal } from './modal';

/** Confirmación de acciones destructivas o sensibles. El padre la muestra con @if. */
@Component({
  selector: 'fc-confirm',
  imports: [Modal, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="title()" (closed)="cancelled.emit()">
      <p>{{ message() }}</p>
      <ng-container footer>
        <button type="button" class="btn" (click)="cancelled.emit()">{{ 'common.cancel' | transloco }}</button>
        <button type="button" class="btn" [class.btn-danger]="danger()" [class.btn-primary]="!danger()" (click)="confirmed.emit()">
          {{ confirmLabel() }}
        </button>
      </ng-container>
    </fc-modal>
  `,
})
export class Confirm {
  readonly title = input.required<string>();
  readonly message = input.required<string>();
  readonly confirmLabel = input.required<string>();
  readonly danger = input(false);
  readonly confirmed = output<void>();
  readonly cancelled = output<void>();
}
