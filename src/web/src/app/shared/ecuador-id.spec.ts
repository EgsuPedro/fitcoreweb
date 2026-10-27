import { isValidId, normalizeId } from './ecuador-id';

describe('documentos de identidad de Ecuador', () => {
  it('acepta cédulas válidas aunque tengan espacios o guiones', () => {
    for (const value of ['1712345675', '0912345675', '171234 5675', '171234-5675']) {
      expect(isValidId('Cedula', value)).toBe(true);
    }
  });

  it('rechaza cédulas con dígito verificador, provincia, tercer dígito, largo o letras incorrectos', () => {
    for (const value of ['1712345676', '9912345675', '1792345675', '171234567', '17123456755', '17123A5675', '']) {
      expect(isValidId('Cedula', value)).toBe(false);
    }
  });

  it('valida RUC de persona natural, sociedad privada y entidad pública', () => {
    for (const value of ['1712345675001', '1790000028001', '1760001550001']) {
      expect(isValidId('Ruc', value)).toBe(true);
    }
    for (const value of ['1712345675000', '1790000029001', '1760001540001', '1760001550000', '1772345675001', '171234567500']) {
      expect(isValidId('Ruc', value)).toBe(false);
    }
  });

  it('valida pasaportes y otros documentos por formato', () => {
    expect(isValidId('Passport', 'AB123456')).toBe(true);
    expect(isValidId('Passport', 'AB12')).toBe(false);
    expect(isValidId('Passport', 'AB12#456')).toBe(false);
    expect(isValidId('Other', 'XYZ')).toBe(true);
    expect(isValidId('Other', 'AB')).toBe(false);
  });

  it('normaliza a mayúsculas sin espacios ni guiones', () => {
    expect(normalizeId(' ab-12 3456 ')).toBe('AB123456');
  });
});