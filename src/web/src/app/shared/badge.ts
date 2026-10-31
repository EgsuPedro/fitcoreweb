import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type Tone = 'ok' | 'warn' | 'danger' | 'info' | 'off' | 'neutral';

/** Indicador de estado con color. El color nunca es lo único que lo distingue: siempre lleva texto. */
@Component({
  selector: 'fc-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="badge" [class]="'badge badge-' + tone()"><ng-content /></span>`,
})
export class Badge {
  readonly tone = input<Tone>('off');
}

export const membershipTone = (status: string): Tone =>
  status === 'Active' ? 'ok' : status === 'PendingPayment' ? 'warn' : status === 'Frozen' ? 'info' : status === 'Expired' ? 'neutral' : 'off';

export const obligationTone = (status: string): Tone =>
  status === 'Paid' ? 'ok' : status === 'Overdue' ? 'danger' : status === 'PartiallyPaid' ? 'warn' : status === 'Pending' ? 'info' : 'off';

export const paymentTone = (status: string): Tone => (status === 'Confirmed' ? 'ok' : status === 'PendingVerification' ? 'warn' : 'off');

export const promotionTone = (status: string): Tone =>
  status === 'Active' ? 'ok' : status === 'Scheduled' ? 'info' : status === 'Exhausted' ? 'warn' : 'off';

/** Estado de un comprobante electrónico del SRI. Lo que espera a una persona (devuelto, no autorizado) se ve en rojo; lo que está en trámite, en amarillo o azul. */
export const invoiceTone = (status: string): Tone =>
  status === 'Authorized'
    ? 'ok'
    : status === 'Rejected' || status === 'NotAuthorized'
      ? 'danger'
      : status === 'Received' || status === 'Processing'
        ? 'info'
        : status === 'PendingSignature' || status === 'SignedDocument' || status === 'PendingSubmission'
          ? 'warn'
          : 'off';
