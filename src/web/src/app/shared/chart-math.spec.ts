import { clampPercent, layoutSeries } from './chart-math';

describe('layoutSeries', () => {
  it('no dibuja nada sin datos', () => {
    expect(layoutSeries([], 500, 200, 20)).toEqual({ points: [], min: 0, max: 0, line: '' });
  });

  it('reparte los puntos a intervalos iguales y ordena el eje vertical de menor a mayor', () => {
    const layout = layoutSeries(
      [
        { label: 'a', value: 80 },
        { label: 'b', value: 78 },
        { label: 'c', value: 75 },
      ],
      500,
      200,
      20,
    );

    expect(layout.points.map((p) => p.x)).toEqual([20, 250, 480]);
    // Un valor menor queda más abajo en pantalla (y mayor).
    expect(layout.points[2].y).toBeGreaterThan(layout.points[0].y);
    expect(layout.min).toBeLessThan(75);
    expect(layout.max).toBeGreaterThan(80);
    expect(layout.line.split(' ')).toHaveLength(3);
  });

  it('un solo punto queda centrado y una serie constante no divide entre cero', () => {
    const single = layoutSeries([{ label: 'a', value: 70 }], 500, 200, 20);
    expect(single.points[0].x).toBe(250);
    expect(Number.isFinite(single.points[0].y)).toBe(true);

    const flat = layoutSeries(
      [
        { label: 'a', value: 70 },
        { label: 'b', value: 70 },
      ],
      500,
      200,
      20,
    );
    expect(flat.points.every((p) => Number.isFinite(p.y))).toBe(true);
    expect(flat.points[0].y).toBe(flat.points[1].y);
  });
});

describe('clampPercent', () => {
  it('acota a 0–100 y trata lo ausente como 0', () => {
    expect(clampPercent(150)).toBe(100);
    expect(clampPercent(-5)).toBe(0);
    expect(clampPercent(42)).toBe(42);
    expect(clampPercent(null)).toBe(0);
    expect(clampPercent(undefined)).toBe(0);
    expect(clampPercent(Number.NaN)).toBe(0);
  });
});
