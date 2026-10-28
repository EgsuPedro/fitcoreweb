import { toInt, toLines, toNumber, todayIso } from './form-utils';

describe('form-utils', () => {
  it('convierte números con coma o punto y trata lo vacío o inválido como nulo', () => {
    expect(toNumber('72,5')).toBe(72.5);
    expect(toNumber(' 72.5 ')).toBe(72.5);
    expect(toNumber('')).toBeNull();
    expect(toNumber('   ')).toBeNull();
    expect(toNumber('abc')).toBeNull();
    expect(toInt('12.9')).toBe(12);
    expect(toInt('')).toBeNull();
  });

  it('separa líneas sin vacíos', () => {
    expect(toLines(' Maní \n\n  Mariscos  \n')).toEqual(['Maní', 'Mariscos']);
    expect(toLines('')).toEqual([]);
  });

  it('da la fecha de hoy como yyyy-MM-dd', () => {
    expect(todayIso()).toMatch(/^\d{4}-\d{2}-\d{2}$/);
  });
});
