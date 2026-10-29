import type { Tone } from '../../shared/badge';

const SEVERITY_ORDER: Record<string, number> = { Critical: 0, Warning: 1, Info: 2 };

/** Clase del aviso según su gravedad. El texto siempre acompaña al color. */
export function alertClass(severity: string): string {
  switch (severity) {
    case 'Critical':
      return 'alert-danger';
    case 'Warning':
      return 'alert-warning';
    default:
      return 'alert-info';
  }
}

/** Lo más grave primero; a igual gravedad se conserva el orden en que llegó. */
export function sortBySeverity<T extends { severity: string }>(items: readonly T[]): T[] {
  return items
    .map((item, index) => ({ item, index }))
    .sort((a, b) => (SEVERITY_ORDER[a.item.severity] ?? 3) - (SEVERITY_ORDER[b.item.severity] ?? 3) || a.index - b.index)
    .map((x) => x.item);
}

/** Porcentaje de un límite ya consumido (0 a 100). Sin límite o sin medir no hay porcentaje. */
export function usagePercent(used: number | null, limit: number | null): number | null {
  if (used === null || limit === null || limit <= 0) {
    return null;
  }

  return Math.min(100, Math.round((used * 100) / limit));
}

export function requestTone(status: string): Tone {
  switch (status) {
    case 'Approved':
      return 'ok';
    case 'Pending':
      return 'info';
    case 'Rejected':
      return 'danger';
    default:
      return 'off';
  }
}

/** ¿El modo de acceso limita lo que el gimnasio puede hacer? */
export const isRestricted = (accessMode: string): boolean => accessMode === 'Restricted';
