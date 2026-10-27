import { looksLikeCardNumber } from './card-number';

describe('detección de números de tarjeta', () => {
  it('reconoce números válidos con o sin separadores', () => {
    for (const value of ['4111111111111111', '4111 1111 1111 1111', '5500-0000-0000-0004']) {
      expect(looksLikeCardNumber(value)).toBe(true);
    }
  });

  it('no confunde una referencia de voucher con un número de tarjeta', () => {
    for (const value of ['VCH-123456', 'TRX-884211', '1234567890123456', '12345', '']) {
      expect(looksLikeCardNumber(value)).toBe(false);
    }
  });
});