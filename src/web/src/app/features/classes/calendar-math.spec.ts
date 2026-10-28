import { describe, expect, it } from 'vitest';
import { addDays, dateKeyOf, partsOf, timeKeyOf, toInstant, weekdayOf } from '../../shared/zoned-time';
import { datesBetween, endOfMonth, groupByDate, monthGrid, rangeOf, shift, startOfMonth, startOfWeek } from './calendar-math';

describe('hora de la sucursal', () => {
  it('convierte un instante a la fecha y hora de Ecuador (UTC-5)', () => {
    expect(dateKeyOf('2026-11-02T14:00:00Z', 'America/Guayaquil')).toBe('2026-11-02');
    expect(timeKeyOf('2026-11-02T14:00:00Z', 'America/Guayaquil')).toBe('09:00');
    // Pasada la medianoche UTC sigue siendo el día anterior en Ecuador.
    expect(dateKeyOf('2026-11-03T02:30:00Z', 'America/Guayaquil')).toBe('2026-11-02');
    expect(timeKeyOf('2026-11-03T02:30:00Z', 'America/Guayaquil')).toBe('21:30');
  });

  it('muestra la misma hora de pared sea cual sea la zona del navegador', () => {
    expect(partsOf('2026-11-02T14:00:00Z', 'America/Guayaquil')).toMatchObject({ year: 2026, month: 11, day: 2, hour: 9, minute: 0 });
    expect(partsOf('2026-11-02T14:00:00Z', 'Pacific/Galapagos')).toMatchObject({ hour: 8 });
  });

  it('convierte fecha y hora locales a un instante UTC', () => {
    expect(toInstant('2026-11-02', '09:00', 'America/Guayaquil')).toBe('2026-11-02T14:00:00.000Z');
    expect(toInstant('2026-11-02', '23:30', 'America/Guayaquil')).toBe('2026-11-03T04:30:00.000Z');
    expect(toInstant('2026-11-02', '08:00', 'Pacific/Galapagos')).toBe('2026-11-02T14:00:00.000Z');
  });

  it('ida y vuelta: lo que se programa es lo que se ve', () => {
    for (const time of ['00:00', '06:15', '12:00', '23:59']) {
      const instant = toInstant('2026-12-31', time, 'America/Guayaquil');
      expect(dateKeyOf(instant, 'America/Guayaquil')).toBe('2026-12-31');
      expect(timeKeyOf(instant, 'America/Guayaquil')).toBe(time);
    }
  });

  it('respeta el horario de verano de otras zonas', () => {
    // Nueva York: UTC-4 el 31 de octubre de 2026 y UTC-5 desde el 1 de noviembre.
    expect(toInstant('2026-10-31', '09:00', 'America/New_York')).toBe('2026-10-31T13:00:00.000Z');
    expect(toInstant('2026-11-02', '09:00', 'America/New_York')).toBe('2026-11-02T14:00:00.000Z');
  });

  it('suma días y calcula el día de la semana sin depender de la zona', () => {
    expect(addDays('2026-12-30', 3)).toBe('2027-01-02');
    expect(addDays('2026-03-01', -1)).toBe('2026-02-28');
    expect(weekdayOf('2026-11-02')).toBe(1); // lunes
    expect(weekdayOf('2026-11-01')).toBe(0); // domingo
  });
});

describe('calendario', () => {
  it('la semana empieza el lunes', () => {
    expect(startOfWeek('2026-11-02')).toBe('2026-11-02');
    expect(startOfWeek('2026-11-08')).toBe('2026-11-02'); // domingo
    expect(startOfWeek('2026-11-04')).toBe('2026-11-02');
  });

  it('calcula el rango de cada vista como intervalo semiabierto', () => {
    expect(rangeOf('day', '2026-11-04')).toEqual({ from: '2026-11-04', to: '2026-11-05' });
    expect(rangeOf('week', '2026-11-04')).toEqual({ from: '2026-11-02', to: '2026-11-09' });
    // El mes muestra seis semanas completas, desde el lunes de la primera.
    expect(rangeOf('month', '2026-11-15')).toEqual({ from: '2026-10-26', to: '2026-12-07' });
  });

  it('avanza y retrocede por día, semana y mes (también al cruzar el año)', () => {
    expect(shift('day', '2026-11-30', 1)).toBe('2026-12-01');
    expect(shift('week', '2026-11-04', -1)).toBe('2026-10-28');
    expect(shift('month', '2026-12-15', 1)).toBe('2027-01-01');
    expect(shift('month', '2026-01-15', -1)).toBe('2025-12-01');
  });

  it('arma la cuadrícula del mes con seis filas de siete días', () => {
    const grid = monthGrid('2026-11-15');
    expect(grid).toHaveLength(6);
    expect(grid.every((week) => week.length === 7)).toBe(true);
    expect(grid[0][0]).toBe('2026-10-26');
    expect(grid[5][6]).toBe('2026-12-06');
    expect(grid.flat()).toContain('2026-11-30');
  });

  it('calcula el primero y el último día del mes', () => {
    expect(startOfMonth('2026-02-17')).toBe('2026-02-01');
    expect(endOfMonth('2026-02-17')).toBe('2026-02-28');
    expect(endOfMonth('2028-02-10')).toBe('2028-02-29');
    expect(endOfMonth('2026-12-05')).toBe('2026-12-31');
  });

  it('lista las fechas de un rango y agrupa por día', () => {
    expect(datesBetween('2026-11-02', '2026-11-05')).toEqual(['2026-11-02', '2026-11-03', '2026-11-04']);
    const groups = groupByDate(['a1', 'b1', 'a2'], (item) => item[0]);
    expect([...groups.keys()]).toEqual(['a', 'b']);
    expect(groups.get('a')).toEqual(['a1', 'a2']);
  });
});
