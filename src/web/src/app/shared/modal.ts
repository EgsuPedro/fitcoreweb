import { AfterViewInit, ChangeDetectionStrategy, Component, ElementRef, input, output, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * Diálogo modal basado en el elemento nativo <dialog>: el navegador gestiona el foco, Esc y el fondo inerte.
 * El padre lo muestra con @if y reacciona a (closed).
 */
@Component({
  selector: 'fc-modal',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dialog #dialog class="modal" [attr.aria-label]="title()" (close)="closed.emit()" (click)="onBackdrop($event)">
      <div class="modal-panel">
        <header class="modal-header">
          <h2>{{ title() }}</h2>
          <button type="button" class="btn btn-ghost btn-icon" [attr.aria-label]="'common.close' | transloco" (click)="close()">
            ×
          </button>
        </header>
        <div class="modal-body"><ng-content /></div>
        <footer class="modal-footer"><ng-content select="[footer]" /></footer>
      </div>
    </dialog>
  `,
})
export class Modal implements AfterViewInit {
  readonly title = input.required<string>();
  readonly closed = output<void>();

  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');

  ngAfterViewInit(): void {
    this.dialog().nativeElement.showModal();
  }

  close(): void {
    this.dialog().nativeElement.close();
  }

  onBackdrop(event: MouseEvent): void {
    // Un clic sobre el propio <dialog> (fuera del panel) es un clic en el fondo.
    if (event.target === this.dialog().nativeElement) {
      this.close();
    }
  }
}
