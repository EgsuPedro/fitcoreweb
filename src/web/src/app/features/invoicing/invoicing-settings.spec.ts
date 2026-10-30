import { describe, expect, it } from 'vitest';
import { looksLikeRuc } from './invoicing-settings';

describe('looksLikeRuc', () => {
  it('acepta 13 dígitos terminados en 001, con espacios o guiones', () => {
    expect(looksLikeRuc('1790000028001')).toBe(true);
    expect(looksLikeRuc('1790 0000 28001')).toBe(true);
  });

  it('rechaza longitudes distintas, letras y los sufijos que no son 001', () => {
    for (const value of ['', '1234', '179000002800', '17900000280011', '17900000280AB', '1790000028000', '1790000028002']) {
      expect(looksLikeRuc(value)).toBe(false);
    }
  });
});
