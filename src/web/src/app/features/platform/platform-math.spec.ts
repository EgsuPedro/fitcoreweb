import { daysBetween, emptyToNull, invoiceTone, isFree, monthlyPrice, parseAmount, parseLimit, share, subscriptionTone } from './platform-math';

describe('platform-math', () => {
  it('cada estado de suscripción y de factura tiene un tono propio y los desconocidos son neutros', () => {
    expect(subscriptionTone('Active')).toBe('ok');
    expect(subscriptionTone('Trialing')).toBe('info');
    expect(subscriptionTone('PastDue')).toBe('warn');
    expect(subscriptionTone('Suspended')).toBe('danger');
    expect(subscriptionTone('Expired')).toBe('danger');
    expect(subscriptionTone('Cancelled')).toBe('off');
    expect(invoiceTone('Paid')).toBe('ok');
    expect(invoiceTone('Failed')).toBe('warn');
    expect(invoiceTone('Void')).toBe('off');
  });

  it('cuenta días enteros entre fechas, también a través de meses y años, y es negativo hacia el pasado', () => {
    expect(daysBetween('2026-10-29', '2026-11-12')).toBe(14);
    expect(daysBetween('2026-12-25', '2027-01-05')).toBe(11);
    expect(daysBetween('2028-02-28', '2028-03-01')).toBe(2);
    expect(daysBetween('2026-10-29', '2026-10-29')).toBe(0);
    expect(daysBetween('2026-11-01', '2026-10-29')).toBe(-3);
  });

  it('normaliza el precio a un mes según la periodicidad', () => {
    expect(monthlyPrice(30, 'Monthly')).toBe(30);
    expect(monthlyPrice(90, 'Quarterly')).toBe(30);
    expect(monthlyPrice(120, 'Yearly')).toBe(10);
    expect(monthlyPrice(15, 'Otra')).toBe(15);
    expect(isFree(0)).toBe(true);
    expect(isFree(0.01)).toBe(false);
  });

  it('un límite vacío es «sin límite», uno válido es un entero y el resto no es válido', () => {
    expect(parseLimit('')).toBeNull();
    expect(parseLimit('  ')).toBeNull();
    expect(parseLimit('0')).toBe(0);
    expect(parseLimit(' 250 ')).toBe(250);
    expect(Number.isNaN(parseLimit('-1'))).toBe(true);
    expect(Number.isNaN(parseLimit('1.5'))).toBe(true);
    expect(Number.isNaN(parseLimit('abc'))).toBe(true);
  });

  it('el importe admite coma o punto y hasta dos decimales', () => {
    expect(parseAmount('40')).toBe(40);
    expect(parseAmount('40,5')).toBe(40.5);
    expect(parseAmount('49.90')).toBe(49.9);
    expect(Number.isNaN(parseAmount('10.999'))).toBe(true);
    expect(Number.isNaN(parseAmount('-5'))).toBe(true);
    expect(Number.isNaN(parseAmount(''))).toBe(true);
    expect(Number.isNaN(parseAmount('1e3'))).toBe(true);
  });

  it('el texto vacío se envía como null y las barras no dividen por cero', () => {
    expect(emptyToNull('  ')).toBeNull();
    expect(emptyToNull(undefined)).toBeNull();
    expect(emptyToNull(' hola ')).toBe('hola');
    expect(share(1, 4)).toBe(25);
    expect(share(2, 3)).toBe(67);
    expect(share(5, 0)).toBe(0);
  });
});
