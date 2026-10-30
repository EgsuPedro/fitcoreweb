import { describe, expect, it } from 'vitest';
import { defaultHours, hoursProblem, rowsFromServer, rowsToRequest } from './onboarding-hours';

describe('horario del asistente', () => {
  it('sugiere lunes a viernes, sábado corto y domingo cerrado', () => {
    const rows = defaultHours();
    expect(rows.map((r) => r.day)).toEqual([1, 2, 3, 4, 5, 6, 0]);
    expect(rows.find((r) => r.day === 0)?.closed).toBe(true);
    expect(rows.find((r) => r.day === 6)).toMatchObject({ open: '08:00', close: '14:00', closed: false });
  });

  it('sin horario guardado usa el sugerido', () => {
    expect(rowsFromServer(undefined)).toEqual(defaultHours());
    expect(rowsFromServer([])).toEqual(defaultHours());
  });

  it('con horario guardado respeta lo guardado y cierra los días que faltan', () => {
    const rows = rowsFromServer([{ day: 1, closed: false, open: '07:00', close: '20:00' }]);
    expect(rows.find((r) => r.day === 1)).toMatchObject({ open: '07:00', close: '20:00', closed: false });
    expect(rows.find((r) => r.day === 2)?.closed).toBe(true);
  });

  it('los días cerrados se envían sin horas', () => {
    const request = rowsToRequest([
      { day: 0, closed: true, open: '06:00', close: '21:00' },
      { day: 1, closed: false, open: '06:00', close: '21:00' },
    ]);
    expect(request[0]).toEqual({ day: 0, closed: true, open: null, close: null });
    expect(request[1]).toEqual({ day: 1, closed: false, open: '06:00', close: '21:00' });
  });

  it('detecta apertura igual o posterior al cierre y horas mal escritas, pero solo en días abiertos', () => {
    expect(hoursProblem(defaultHours())).toBeNull();
    expect(hoursProblem([{ day: 2, closed: false, open: '21:00', close: '06:00' }])).toEqual({ day: 2 });
    expect(hoursProblem([{ day: 2, closed: false, open: '08:00', close: '08:00' }])).toEqual({ day: 2 });
    expect(hoursProblem([{ day: 3, closed: false, open: '6am', close: '9pm' }])).toEqual({ day: 3 });
    expect(hoursProblem([{ day: 0, closed: true, open: '99:99', close: 'x' }])).toBeNull();
  });
});
