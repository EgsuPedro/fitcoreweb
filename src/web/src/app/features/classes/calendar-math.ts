import { addDays, weekdayOf } from '../../shared/zoned-time';

export type CalendarView = 'day' | 'week' | 'month';

/** Lunes de la semana de una fecha (la semana empieza el lunes). */
export function startOfWeek(dateKey: string): string {
  const weekday = weekdayOf(dateKey);
  return addDays(dateKey, -((weekday + 6) % 7));
}

export function startOfMonth(dateKey: string): string {
  return `${dateKey.slice(0, 7)}-01`;
}

/** Último día del mes de una fecha. */
export function endOfMonth(dateKey: string): string {
  const [year, month] = dateKey.split('-').map(Number);
  return addDays(`${String(month === 12 ? year + 1 : year).padStart(4, '0')}-${String(month === 12 ? 1 : month + 1).padStart(2, '0')}-01`, -1);
}

/** Primer día mostrado y primer día NO mostrado (rango semiabierto) de la vista que contiene la fecha. */
export function rangeOf(view: CalendarView, dateKey: string): { from: string; to: string } {
  switch (view) {
    case 'day':
      return { from: dateKey, to: addDays(dateKey, 1) };
    case 'week': {
      const from = startOfWeek(dateKey);
      return { from, to: addDays(from, 7) };
    }
    default: {
      const from = startOfWeek(startOfMonth(dateKey));
      return { from, to: addDays(from, 42) };
    }
  }
}

/** Fecha de referencia de la vista anterior (-1) o siguiente (+1). */
export function shift(view: CalendarView, dateKey: string, direction: -1 | 1): string {
  if (view === 'day') {
    return addDays(dateKey, direction);
  }
  if (view === 'week') {
    return addDays(dateKey, 7 * direction);
  }
  const [year, month] = dateKey.split('-').map(Number);
  const moved = month - 1 + direction;
  const newYear = year + Math.floor(moved / 12);
  const newMonth = ((moved % 12) + 12) % 12;
  return `${String(newYear).padStart(4, '0')}-${String(newMonth + 1).padStart(2, '0')}-01`;
}

/** Semanas (lunes a domingo) de seis filas que cubren el mes de la fecha. */
export function monthGrid(dateKey: string): string[][] {
  const first = startOfWeek(startOfMonth(dateKey));
  return Array.from({ length: 6 }, (_, week) => Array.from({ length: 7 }, (_, day) => addDays(first, week * 7 + day)));
}

/** Las fechas de [from, to) en orden. */
export function datesBetween(from: string, to: string): string[] {
  const dates: string[] = [];
  for (let date = from; date < to; date = addDays(date, 1)) {
    dates.push(date);
  }
  return dates;
}

/** Agrupa por fecha local de la sucursal, conservando el orden recibido. */
export function groupByDate<T>(items: readonly T[], dateOf: (item: T) => string): Map<string, T[]> {
  const groups = new Map<string, T[]>();
  for (const item of items) {
    const key = dateOf(item);
    const list = groups.get(key);
    if (list) {
      list.push(item);
    } else {
      groups.set(key, [item]);
    }
  }
  return groups;
}
