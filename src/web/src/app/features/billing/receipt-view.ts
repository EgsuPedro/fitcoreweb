import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Receipt } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { FcDateTime, FcMoney } from '../../shared/format.pipes';
import { Modal } from '../../shared/modal';

/** Recibo interno de un pago. No es una factura tributaria (lo aclara el propio recibo). */
@Component({
  selector: 'fc-receipt-view',
  imports: [TranslocoPipe, Modal, FcMoney, FcDateTime],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="'billing.receipt.title' | transloco" (closed)="closed.emit()">
      @if (error()) {
        <p class="alert alert-danger" role="alert">{{ error() }}</p>
      } @else if (receipt(); as r) {
        <article class="receipt">
          <h3>{{ r.receiptNumber }}</h3>
          <p class="receipt-notice">{{ r.notice }}</p>
          <dl class="kv">
            <dt>{{ 'common.date' | transloco }}</dt>
            <dd>{{ r.issuedAt | fcDateTime }}</dd>
            <dt>{{ 'billing.receipt.branch' | transloco }}</dt>
            <dd>{{ r.branchName }} ({{ r.branchCode }})</dd>
            <dt>{{ 'billing.receipt.member' | transloco }}</dt>
            <dd>{{ r.memberName }} · {{ r.memberCode }}</dd>
            <dt>{{ 'billing.receipt.method' | transloco }}</dt>
            <dd>
              {{ 'enums.paymentMethod.' + r.method | transloco }}
              @if (r.methodDetail) {
                · {{ r.methodDetail }}
              }
              @if (r.reference) {
                · {{ r.reference }}
              }
            </dd>
            <dt>{{ 'common.status' | transloco }}</dt>
            <dd>{{ 'enums.paymentStatus.' + r.status | transloco }}</dd>
            @if (r.receivedBy) {
              <dt>{{ 'billing.receipt.receivedBy' | transloco }}</dt>
              <dd>{{ r.receivedBy }}</dd>
            }
          </dl>
          <table class="table">
            <thead>
              <tr>
                <th>{{ 'billing.receipt.line' | transloco }}</th>
                <th class="num">{{ 'billing.receipt.allocated' | transloco }}</th>
                <th class="num">{{ 'billing.receipt.currentBalance' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (line of r.lines; track line.obligationReference) {
                <tr>
                  <td>{{ line.obligationReference }} · {{ line.concept }}</td>
                  <td class="num">{{ line.amount | fcMoney }}</td>
                  <td class="num">{{ line.obligationBalance | fcMoney }}</td>
                </tr>
              }
            </tbody>
          </table>
          <p class="receipt-total">{{ 'common.total' | transloco }}: {{ r.total | fcMoney }}</p>
        </article>
      } @else {
        <p class="muted">{{ 'common.loading' | transloco }}</p>
      }
      <ng-container footer>
        <button type="button" class="btn" (click)="print()">{{ 'common.print' | transloco }}</button>
        <button type="button" class="btn btn-primary" (click)="closed.emit()">{{ 'common.close' | transloco }}</button>
      </ng-container>
    </fc-modal>
  `,
})
export class ReceiptView implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);

  readonly paymentId = input.required<string>();
  readonly closed = output<void>();

  protected readonly receipt = signal<Receipt | null>(null);
  protected readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.api.receipt(this.paymentId()).subscribe({
      next: (receipt) => this.receipt.set(receipt),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  protected print(): void {
    window.print();
  }
}