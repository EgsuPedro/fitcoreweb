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