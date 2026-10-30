import { describe, expect, it } from 'vitest';
import { looksLikeRuc } from './invoicing-settings';

describe('looksLikeRuc', () => {
  it('acepta 13 dígitos, con espacios o guiones', () => {
    expect(looksLikeRuc('1790000028001')).toBe(true);
    expect(looksLikeRuc('1790 0000 28001')).toBe(true);
  });

  it('rechaza longitudes distintas, letras y el sufijo 000', () => {
    for (const value of ['', '1234', '179000002800', '17900000280011', '17900000280AB', '1790000028000']) {
      expect(looksLikeRuc(value)).toBe(false);
    }
  });
});
