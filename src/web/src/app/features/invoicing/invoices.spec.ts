import { invoiceTone } from '../../shared/badge';
import type { SriInvoice, SriInvoicingSummary } from '../../core/api/models';
import { canDiscard, canSubmit, isActive, lastSriMessages } from './invoice-detail';
import { splitIssues } from './invoice-issue';
import { countOf } from './invoices';

type Transmission = SriInvoice['transmission'];

const queued: Transmission = { parked: false, attempts: 0, polls: 0, lastError: null, nextAttemptAt: '', firstAttemptAt: '', lastAttemptAt: null };
const parked: Transmission = { ...queued, parked: true };

describe('estados del comprobante', () => {
  it('colorea lo que espera a una persona en rojo y lo autorizado en verde', () => {
    expect(invoiceTone('Authorized')).toBe('ok');
    expect(invoiceTone('Rejected')).toBe('danger');
    expect(invoiceTone('NotAuthorized')).toBe('danger');
    expect(invoiceTone('Processing')).toBe('info');
    expect(invoiceTone('PendingSignature')).toBe('warn');
    expect(invoiceTone('Discarded')).toBe('off');
  });

  it('solo refresca la pantalla mientras el comprobante está en la cola', () => {
    expect(isActive({ status: 'Received', transmission: queued })).toBe(true);
    expect(isActive({ status: 'Received', transmission: parked })).toBe(false);
    expect(isActive({ status: 'PendingSignature', transmission: null })).toBe(false);
    expect(isActive({ status: 'Authorized', transmission: null })).toBe(false);
  });
});

describe('qué se puede hacer con un comprobante', () => {
  it('descarta lo devuelto, lo no autorizado y lo que nunca se envió, pero no lo que está en trámite ni lo autorizado', () => {
    expect(canDiscard({ status: 'Rejected', sriAttempts: [] })).toBe(true);
    expect(canDiscard({ status: 'NotAuthorized', sriAttempts: [] })).toBe(true);
    expect(canDiscard({ status: 'PendingSignature', sriAttempts: [] })).toBe(true);
    expect(canDiscard({ status: 'SignedDocument', sriAttempts: [] })).toBe(true);
    for (const status of ['Received', 'Processing', 'PendingSubmission', 'Authorized', 'Voided', 'Discarded']) {
      expect(canDiscard({ status, sriAttempts: [] })).toBe(false);
    }
  });

  it('no descarta uno sin enviar si ya hubo un intercambio con el SRI', () => {
    const attempt = { operation: 'Reception', outcome: 'Unavailable', sriState: null, errorCode: 'sri_timeout', durationMs: 5, messages: [], at: '' };
    expect(canDiscard({ status: 'SignedDocument', sriAttempts: [attempt] })).toBe(false);
  });

  it('ofrece enviar lo nunca encolado, lo devuelto y lo estacionado, pero no lo que ya está en la cola', () => {
    expect(canSubmit({ status: 'PendingSignature', transmission: null })).toBe(true);
    expect(canSubmit({ status: 'Rejected', transmission: null })).toBe(true);
    expect(canSubmit({ status: 'PendingSubmission', transmission: parked })).toBe(true);
    expect(canSubmit({ status: 'PendingSignature', transmission: queued })).toBe(false);
    expect(canSubmit({ status: 'Received', transmission: queued })).toBe(false);
    expect(canSubmit({ status: 'Authorized', transmission: null })).toBe(false);
    expect(canSubmit({ status: 'NotAuthorized', transmission: null })).toBe(false);
    expect(canSubmit({ status: 'Discarded', transmission: null })).toBe(false);
  });

  it('muestra los mensajes del último intercambio que los trajo', () => {
    const message = { id: '45', text: 'ERROR SECUENCIAL REGISTRADO', detail: null, type: 'ERROR' };
    const at = (messages: (typeof message)[]) => ({ operation: 'Reception', outcome: 'Returned', sriState: 'DEVUELTA', errorCode: null, durationMs: 1, messages, at: '' });

    expect(lastSriMessages({ sriAttempts: [at([]), at([message])] })).toEqual([message]);
    expect(lastSriMessages({ sriAttempts: [] })).toEqual([]);
  });
});

describe('emitir una factura', () => {
  it('separa lo que impide emitir de los simples avisos', () => {
    const result = splitIssues({
      issues: [
        { code: 'cedula_invalid', blocking: true },
        { code: 'email_missing', blocking: false },
        { code: 'already_invoiced', blocking: true },
      ],
    });

    expect(result).toEqual({ blocking: ['cedula_invalid', 'already_invoiced'], warnings: ['email_missing'] });
  });
});

describe('panel', () => {
  const summary = { countsByStatus: { PendingSignature: 2, Received: 1, Processing: 3, Authorized: 9 } } as unknown as SriInvoicingSummary;

  it('suma los comprobantes de varios estados y tolera los que no vienen', () => {
    expect(countOf(summary, ['PendingSignature', 'SignedDocument', 'Received', 'Processing'])).toBe(6);
    expect(countOf(summary, ['Rejected'])).toBe(0);
    expect(countOf(null, ['Authorized'])).toBe(0);
  });
});
