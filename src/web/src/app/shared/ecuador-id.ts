export type IdType = 'Cedula' | 'Ruc' | 'Passport' | 'Other';

export const ID_TYPES: readonly IdType[] = ['Cedula', 'Ruc', 'Passport', 'Other'];

export function normalizeId(value: string): string {
  return value.replace(/[\s-]/g, '').toUpperCase();
}

const provinceOk = (value: string): boolean => {
  const province = Number(value.slice(0, 2));
  return (province >= 1 && province <= 24) || province === 30;
};

function naturalPerson(first10: string): boolean {
  if (!provinceOk(first10) || Number(first10[2]) >= 6) {
    return false;
  }
  let sum = 0;
  for (let i = 0; i < 9; i++) {
    const product = Number(first10[i]) * (i % 2 === 0 ? 2 : 1);
    sum += product > 9 ? product - 9 : product;
  }
  return (10 - (sum % 10)) % 10 === Number(first10[9]);
}

function modulo11(digits: string, coefficients: number[], check: number): boolean {
  const sum = coefficients.reduce((acc, c, i) => acc + Number(digits[i]) * c, 0);
  const residue = sum % 11;
  const expected = residue === 0 ? 0 : 11 - residue;
  return expected !== 10 && expected === check;
}

/**
 * Validación inmediata de cédula, RUC y pasaporte para dar respuesta al escribir. Es solo una ayuda de usabilidad:
 * las mismas reglas las aplica el backend, que es la autoridad.
 */
export function isValidId(type: IdType, raw: string): boolean {
  const value = normalizeId(raw);
  switch (type) {
    case 'Cedula':
      return /^\d{10}$/.test(value) && naturalPerson(value);
    case 'Ruc': {
      if (!/^\d{13}$/.test(value) || !provinceOk(value)) {
        return false;
      }
      const third = Number(value[2]);
      if (third < 6) {
        return naturalPerson(value.slice(0, 10)) && value.slice(10) !== '000';
      }
      if (third === 6) {
        return modulo11(value.slice(0, 8), [3, 2, 7, 6, 5, 4, 3, 2], Number(value[8])) && value.slice(9) !== '0000';
      }
      return third === 9 && modulo11(value.slice(0, 9), [4, 3, 2, 7, 6, 5, 4, 3, 2], Number(value[9])) && value.slice(10) !== '000';
    }
    case 'Passport':
      return /^[A-Z0-9]{5,20}$/.test(value);
    default:
      return /^[A-Z0-9]{3,20}$/.test(value);
  }
}