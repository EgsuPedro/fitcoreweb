import type { SriCreditNotePreview } from '../../core/api/models';
import { buildCreditLines, cents, parseAmount, type LineChoice } from './credit-note-issue';

const preview = {
  lines: [
    { lineId: 'a', lineNumber: 1, description: 'Membresía', taxPercent: 15, originalAmount: 30, creditedAmount: 0, remainingAmount: 30 },
    { lineId: 'b', lineNumber: 2, description: 'Matrícula', taxPercent: 15, originalAmount: 10, creditedAmount: 4, remainingAmount: 6 },
  ],
} as Pick<SriCreditNotePreview, 'lines'>;

const choices = (a: Partial<LineChoice>, b: Partial<LineChoice> = {}): Record<string, LineChoice> => ({
  a: { included: true, amount: '30.00', ...a },
  b: { included: false, amount: '6.00', ...b },
});

describe('importe a acreditar', () => {
  it('acepta coma o punto y hasta dos decimales', () => {
    expect(parseAmount('11.50')).toBe(11.5);
    expect(parseAmount('11,5')).toBe(11.5);
    expect(parseAmount(' 7 ')).toBe(7);
  });

  it('rechaza lo que no es un importe válido', () => {
    for (const text of ['', 'abc', '0', '0.00', '-5', '10.123', '1,2,3', '1e3']) {
      expect(parseAmount(text)).toBeNull();
    }
  });

  it('compara en centavos sin errores de coma flotante', () => {
    expect(cents(0.1 + 0.2)).toBe(cents(0.3));
  });
});

describe('líneas de la nota de crédito', () => {
  it('pide solo las líneas incluidas, con su importe', () => {
    expect(buildCreditLines(preview, choices({ amount: '11.50' }))).toEqual([{ lineId: 'a', amount: 11.5 }]);
    expect(buildCreditLines(preview, choices({}, { included: true }))).toEqual([
      { lineId: 'a', amount: 30 },
      { lineId: 'b', amount: 6 },
    ]);
  });

  it('no deja pedir más de lo que queda de una línea ni un importe inválido', () => {
    expect(buildCreditLines(preview, choices({ amount: '30.01' }))).toBeNull();
    expect(buildCreditLines(preview, choices({}, { included: true, amount: '6.01' }))).toBeNull();
    expect(buildCreditLines(preview, choices({ amount: 'x' }))).toBeNull();
  });

  it('exige al menos una línea', () => {
    expect(buildCreditLines(preview, choices({ included: false }))).toBeNull();
  });
});
