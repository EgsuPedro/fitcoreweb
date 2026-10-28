/** Zona horaria del producto cuando no se conoce la de la sucursal. */
export const DEFAULT_TIME_ZONE = 'America/Guayaquil';

export interface ZonedParts {
  year: number;
  month: number;
  day: number;
  hour: number;
  minute: number;
}

const formatters = new Map<string, Intl.DateTimeFormat>();

function formatterFor(timeZone: string): Intl.DateTimeFormat {
  let formatter = formatters.get(timeZone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('en-US', {
      timeZone,
      hourCycle: 'h23',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    });
    formatters.set(timeZone, formatter);
  }
  return formatter;
}

/**
 * Fecha y hora de un instante en la zona horaria de la sucursal. Las sesiones se programan y se muestran siempre en la hora de la
 * sucursal, no en la del navegador: así una sesión de las 09:00 en Quito se ve a las 09:00 aunque quien mira esté en otro país.
 */
export function partsOf(instant: string | Date, timeZone: string = DEFAULT_TIME_ZONE): ZonedParts {
  const date = typeof instant === 'string' ? new Date(instant) : instant;
  const values: Record<string, number> = {};
  for (const part of formatterFor(timeZone).formatToParts(date)) {
    if (part.type !== 'literal') {
      values[part.type] = Number(part.value);
    }
  }
  return { year: values['year'], month: values['month'], day: values['day'], hour: values['hour'] % 24, minute: values['minute'] };
}

const pad = (value: number, width = 2): string => String(value).padStart(width, '0');

/** `2026-11-02` */
export function dateKeyOf(instant: string | Date, timeZone: string = DEFAULT_TIME_ZONE): string {
  const p = partsOf(instant, timeZone);
  return `${pad(p.year, 4)}-${pad(p.month)}-${pad(p.day)}`;
}

/** `09:30` */
export function timeKeyOf(instant: string | Date, timeZone: string = DEFAULT_TIME_ZONE): string {
  const p = partsOf(instant, timeZone);
  return `${pad(p.hour)}:${pad(p.minute)}`;
}

/** Diferencia (ms) entre la hora de pared de la zona y UTC en un instante. */
function offsetMs(utcMs: number, timeZone: string): number {
  const p = partsOf(new Date(utcMs), timeZone);
  return Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute) - Math.floor(utcMs / 60000) * 60000;
}

/**
 * Instante UTC (ISO) de una fecha `YYYY-MM-DD` y una hora `HH:mm` locales de la zona. Se corrige una vez por si el desfase cambia
 * entre la estimación y el resultado (horario de verano).
 */
export function toInstant(date: string, time: string, timeZone: string = DEFAULT_TIME_ZONE): string {
  const [year, month, day] = date.split('-').map(Number);
  const [hour, minute] = time.split(':').map(Number);
  const wall = Date.UTC(year, month - 1, day, hour, minute);
  let utc = wall - offsetMs(wall, timeZone);
  utc = wall - offsetMs(utc, timeZone);
  return new Date(utc).toISOString();
}

/** Suma días a una fecha `YYYY-MM-DD` (sin zona: es aritmética de calendario). */
export function addDays(dateKey: string, days: number): string {
  const [year, month, day] = dateKey.split('-').map(Number);
  const moved = new Date(Date.UTC(year, month - 1, day + days));
  return `${pad(moved.getUTCFullYear(), 4)}-${pad(moved.getUTCMonth() + 1)}-${pad(moved.getUTCDate())}`;
}

/** Día de la semana de una fecha `YYYY-MM-DD`: 0 = domingo … 6 = sábado. */
export function weekdayOf(dateKey: string): number {
  const [year, month, day] = dateKey.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, day)).getUTCDay();
}
