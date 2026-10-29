import type { Tone } from '../../shared/badge';

/** Color del estado de una suscripción comercial. El texto siempre acompaña al color. */
export function subscriptionTone(status: string): Tone {
  switch (status) {
    case 'Active':
      return 'ok';
    case 'Trialing':
      return 'info';
    case 'PastDue':
      return 'warn';
    case 'Suspended':
    case 'Expired':
      return 'danger';
    default:
      return 'off';
  }
}

export function invoiceTone(status: string): Tone {
  switch (status) {
    case 'Paid':
      return 'ok';
    case 'Pending':
      return 'info';
    case 'Failed':
      return 'warn';
    case 'Refunded':
    case 'PartiallyRefunded':
      return 'neutral';
    default:
      return 'off';
  }
}

/** Días enteros de `from` a `to` (ambos AAAA-MM-DD). Negativo si `to` ya pasó. */
export function daysBetween(from: string, to: string): number {
  const a = Date.UTC(+from.slice(0, 4), +from.slice(5, 7) - 1, +from.slice(8, 10));
  const b = Date.UTC(+to.slice(0, 4), +to.slice(5, 7) - 1, +to.slice(8, 10));
  return Math.round((b - a) / 86_400_000);
}

const MONTHS: Record<string, number> = { Monthly: 1, Quarterly: 3, Yearly: 12 };

/** Precio normalizado a un mes (el mismo criterio con el que el servidor distingue una mejora de una baja de plan). */
export function monthlyPrice(price: number, period: string): number {
  return price / (MONTHS[period] ?? 1);
}

/** Un plan de precio 0 no genera cobros: se renueva solo. */
export const isFree = (price: number): boolean => price === 0;

/** Texto vacío → null (los campos opcionales de la API). */
export const emptyToNull = (value: string | null | undefined): string | null => {
  const text = value?.trim();
  return text ? text : null;
};

/** Valor de un límite del plan: vacío = sin límite (null); un entero ≥ 0 en otro caso; NaN si no es válido. */
export function parseLimit(text: string): number | null {
  const value = text.trim();
  if (value === '') {
    return null;
  }

  return /^\d{1,12}$/.test(value) ? Number(value) : Number.NaN;
}

/** Importe con dos decimales como lo exige la API (sin errores de coma flotante): '40' → 40, '40,5' → 40.5, otro → NaN. */
export function parseAmount(text: string): number {
  const value = text.trim().replace(',', '.');
  return /^\d{1,7}(\.\d{1,2})?$/.test(value) ? Number(value) : Number.NaN;
}

/** Porcentaje de `part` sobre `total` para barras de distribución (0 si no hay total). */
export const share = (part: number, total: number): number => (total <= 0 ? 0 : Math.round((part * 100) / total));
