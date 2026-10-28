import { daysInRange, layoutPair, rangeError, reportLink, variationView } from './analytics-math';

describe('rangos de fechas', () => {
  it('cuenta los días con ambos extremos incluidos', () => {
    expect(daysInRange('2026-03-01', '2026-03-10')).toBe(10);
    expect(daysInRange('2026-03-01', '2026-03-01')).toBe(1);
    expect(daysInRange('2025-03-01', '2026-03-01')).toBe(366);
    expect(daysInRange('2026-02-30', '2026-03-01')).not.toBeNull(); // JS normaliza; el servidor valida la fecha
    expect(daysInRange('no', '2026-03-01')).toBeNull();
  });

  it('solo el rango personalizado se valida', () => {
    expect(rangeError('Last30Days', '', '')).toBeNull();
    expect(rangeError('Custom', '', '')).toBe('analytics.filters.errors.required');
    expect(rangeError('Custom', '2026-03-10', '')).toBe('analytics.filters.errors.required');
    expect(rangeError('Custom', '2026-03-10', '2026-03-01')).toBe('analytics.filters.errors.inverted');
    expect(rangeError('Custom', '2025-03-01', '2026-03-01')).toBeNull(); // 366 días: el límite es inclusivo
    expect(rangeError('Custom', '2025-02-28', '2026-03-01')).toBe('analytics.filters.errors.tooLong');
    expect(rangeError('Custom', '2026-03-01', '2026-03-10')).toBeNull();
  });
});

describe('variación', () => {
  const kpi = (state: string, absolute: number | null, percent: number | null, unit = 'Count') =>
    ({ unit, variation: { state, absolute, percent } }) as Parameters<typeof variationView>[0];

  it('con base muestra el porcentaje y su dirección', () => {
    expect(variationView(kpi('Ok', 20, 100))).toEqual({ kind: 'change', direction: 'up', value: 100, unit: 'percent' });
    expect(variationView(kpi('Ok', -25, -25))).toEqual({ kind: 'change', direction: 'down', value: -25, unit: 'percent' });
    expect(variationView(kpi('Ok', 0, 0))).toEqual({ kind: 'change', direction: 'flat', value: 0, unit: 'percent' });
  });

  it('las razones se comparan en puntos, no en porcentaje de un porcentaje', () => {
    expect(variationView(kpi('Ok', 12.5, null, 'Percent'))).toEqual({ kind: 'change', direction: 'up', value: 12.5, unit: 'points' });
    expect(variationView(kpi('Ok', -50, null, 'Percent'))).toEqual({ kind: 'change', direction: 'down', value: -50, unit: 'points' });
  });

  it('sin base de comparación no hay porcentaje, y los indicadores de hoy no se comparan', () => {
    expect(variationView(kpi('NoBaseline', 75, null))).toEqual({ kind: 'noBaseline' });
    expect(variationView(kpi('NoData', null, null))).toEqual({ kind: 'noData' });
    expect(variationView(kpi('NotApplicable', null, null))).toEqual({ kind: 'none' });
  });
});

describe('enlaces de los indicadores', () => {
  it('cada indicador lleva a su pestaña', () => {
    expect(reportLink('financial/collections')).toEqual({ path: '/analytics/financial', tab: 'collections' });
    expect(reportLink('financial/outstanding-balances')).toEqual({ path: '/analytics/financial', tab: 'outstanding-balances' });
    expect(reportLink('members/retention')).toEqual({ path: '/analytics/members', tab: 'retention' });
    expect(reportLink('classes/occupancy')).toEqual({ path: '/analytics/operations', tab: 'occupancy' });
    expect(reportLink('attendance')).toEqual({ path: '/analytics/operations', tab: 'attendance' });
    expect(reportLink(null)).toBeNull();
    expect(reportLink('algo/raro')).toBeNull();
  });
});

describe('gráfico de dos series', () => {
  it('ambas series comparten la escala y el eje parte de cero', () => {
    const layout = layoutPair([10, 20, 40], [5, 5, 5], 200, 100, 10);
    expect(layout.min).toBe(0);
    expect(layout.max).toBe(40);
    // El valor máximo queda arriba (margen) y el cero abajo; la serie baja nunca supera a la alta.
    expect(layout.current[2].y).toBe(10);
    expect(layout.previous[0].y).toBeGreaterThan(layout.current[0].y);
    expect(layout.current[0].x).toBe(10);
    expect(layout.current[2].x).toBe(190);
  });

  it('sin datos o con todo en cero no divide por cero', () => {
    expect(layoutPair([], [], 200, 100, 10).currentLine).toBe('');
    const flat = layoutPair([0, 0, 0], [0, 0, 0], 200, 100, 10);
    expect(flat.max).toBe(1);
    expect(flat.current.every((p) => Number.isFinite(p.y))).toBe(true);
  });

  it('un solo punto se centra', () => {
    expect(layoutPair([7], [3], 200, 100, 10).current[0].x).toBe(100);
  });
});
