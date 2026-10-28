export interface ChartPoint {
  label: string;
  value: number;
}

export interface PlotPoint extends ChartPoint {
  x: number;
  y: number;
}

export interface ChartLayout {
  points: PlotPoint[];
  min: number;
  max: number;
  /** Polilínea lista para el atributo `points` de SVG. */
  line: string;
}

/**
 * Coloca los puntos de una serie en un área de `width` x `height` con margen `pad`. Los puntos se reparten a intervalos
 * iguales (la serie son mediciones, no un eje de tiempo continuo) y el eje vertical se ajusta al rango de los datos con un
 * pequeño aire arriba y abajo, para que una variación de 0,5 kg se vea sin exagerar un valor casi constante.
 */
export function layoutSeries(series: readonly ChartPoint[], width: number, height: number, pad: number): ChartLayout {
  if (series.length === 0) {
    return { points: [], min: 0, max: 0, line: '' };
  }

  const values = series.map((p) => p.value);
  const low = Math.min(...values);
  const high = Math.max(...values);
  const span = high - low;
  const margin = span === 0 ? Math.max(Math.abs(high) * 0.05, 1) : span * 0.1;
  const min = low - margin;
  const max = high + margin;
  const innerW = width - pad * 2;
  const innerH = height - pad * 2;

  const points = series.map((p, i) => ({
    ...p,
    x: series.length === 1 ? width / 2 : pad + (innerW * i) / (series.length - 1),
    y: pad + innerH - ((p.value - min) / (max - min)) * innerH,
  }));

  return { points, min, max, line: points.map((p) => `${round(p.x)},${round(p.y)}`).join(' ') };
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

/** Porcentaje acotado a 0–100 para anchos de barra; nulo (sin dato) se muestra como 0. */
export function clampPercent(value: number | null | undefined): number {
  return value === null || value === undefined || Number.isNaN(value) ? 0 : Math.min(100, Math.max(0, value));
}
