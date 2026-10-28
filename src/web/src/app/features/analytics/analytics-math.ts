import type { Kpi } from '../../core/api/models';

export const PRESETS = ['Today', 'Last7Days', 'Last30Days', 'CurrentMonth', 'PreviousMonth', 'CurrentYear', 'Custom'] as const;
export type Preset = (typeof PRESETS)[number];

/** El mismo tope que el servidor (366 días): se valida aquí para avisar sin hacer la consulta, pero el servidor decide. */
export const MAX_RANGE_DAYS = 366;

const DAY = 24 * 60 * 60 * 1000;

function toUtc(date: string): number | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(date);
  if (!match) {
    return null;
  }
  const value = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return Number.isNaN(value) ? null : value;
}

/** Días del rango, ambos extremos incluidos; nulo si alguna fecha no es válida. */
export function daysInRange(from: string, to: string): number | null {
  const start = toUtc(from);
  const end = toUtc(to);
  return start === null || end === null ? null : Math.round((end - start) / DAY) + 1;
}

/** Clave de traducción del error de un filtro de fechas, o nulo si es válido. Solo el rango personalizado tiene fechas. */
export function rangeError(preset: Preset, from: string, to: string): string | null {
  if (preset !== 'Custom') {
    return null;
  }
  const days = daysInRange(from, to);
  if (!from || !to || days === null) {
    return 'analytics.filters.errors.required';
  }
  if (days < 1) {
    return 'analytics.filters.errors.inverted';
  }
  return days > MAX_RANGE_DAYS ? 'analytics.filters.errors.tooLong' : null;
}

export type VariationView =
  | { kind: 'change'; direction: 'up' | 'down' | 'flat'; value: number; unit: 'percent' | 'points' | 'absolute' }
  | { kind: 'noBaseline' }
  | { kind: 'noData' }
  | { kind: 'none' };

/**
 * Cómo se muestra la variación de un indicador. Sin base de comparación nunca se inventa un porcentaje; un indicador de «un instante» (saldo de
 * hoy) no se compara. Los porcentajes de razones (ocupación, retención) se dan en puntos, no como porcentaje de un porcentaje.
 */
export function variationView(kpi: Pick<Kpi, 'variation' | 'unit'>): VariationView {
  const v = kpi.variation;
  switch (v.state) {
    case 'Ok': {
      const unit = kpi.unit === 'Percent' ? 'points' : 'percent';
      const value = unit === 'points' ? v.absolute : v.percent;
      if (value === null || value === undefined) {
        return { kind: 'none' };
      }
      return { kind: 'change', direction: value > 0 ? 'up' : value < 0 ? 'down' : 'flat', value, unit };
    }
    case 'NoBaseline':
      return { kind: 'noBaseline' };
    case 'NoData':
      return { kind: 'noData' };
    default:
      return { kind: 'none' };
  }
}

export interface ReportLink {
  path: string;
  tab: string;
}

/** A qué pantalla lleva un indicador (la clave `report` que entrega el servidor). Nulo si no hay reporte detallado. */
export function reportLink(report: string | null | undefined): ReportLink | null {
  if (!report) {
    return null;
  }
  const [area, name] = report.split('/');
  switch (area) {
    case 'financial':
      return { path: '/analytics/financial', tab: name ?? 'collections' };
    case 'members':
      return { path: '/analytics/members', tab: name ?? 'summary' };
    case 'classes':
      return { path: '/analytics/operations', tab: name ?? 'occupancy' };
    case 'attendance':
      return { path: '/analytics/operations', tab: 'attendance' };
    default:
      return null;
  }
}

export interface PairPoint {
  x: number;
  y: number;
}

export interface PairLayout {
  current: PairPoint[];
  previous: PairPoint[];
  min: number;
  max: number;
  currentLine: string;
  previousLine: string;
}

/**
 * Coloca dos series (el período y su comparable) en la misma escala, para poder compararlas a simple vista. El eje parte de cero cuando todos
 * los valores son no negativos: una barra o línea que «sube mucho» por cortar el eje es justo el engaño que se quiere evitar.
 */
export function layoutPair(current: readonly number[], previous: readonly number[], width: number, height: number, pad: number): PairLayout {
  const all = [...current, ...previous];
  const high = all.length === 0 ? 0 : Math.max(...all);
  const low = all.length === 0 ? 0 : Math.min(...all);
  const min = low >= 0 ? 0 : low;
  const max = high === min ? min + 1 : high;
  const innerW = width - pad * 2;
  const innerH = height - pad * 2;
  const place = (series: readonly number[]): PairPoint[] =>
    series.map((value, i) => ({
      x: series.length === 1 ? width / 2 : pad + (innerW * i) / (series.length - 1),
      y: pad + innerH - ((value - min) / (max - min)) * innerH,
    }));
  const line = (points: PairPoint[]) => points.map((p) => `${Math.round(p.x * 10) / 10},${Math.round(p.y * 10) / 10}`).join(' ');
  const currentPoints = place(current);
  const previousPoints = place(previous);
  return { current: currentPoints, previous: previousPoints, min, max, currentLine: line(currentPoints), previousLine: line(previousPoints) };
}
