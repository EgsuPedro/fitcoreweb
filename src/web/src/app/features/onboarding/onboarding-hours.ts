import type { DayHours } from '../../core/api/models';

/** Orden en que se muestran los días: lunes a domingo (el servidor numera 0 = domingo). */
export const DAY_ORDER = [1, 2, 3, 4, 5, 6, 0] as const;

export interface HoursRow {
  day: number;
  closed: boolean;
  open: string;
  close: string;
}

/** Horario sugerido para un gimnasio nuevo: lunes a viernes de 06:00 a 21:00, sábado de 08:00 a 14:00 y domingo cerrado. */
export function defaultHours(): HoursRow[] {
  return DAY_ORDER.map((day) => ({
    day,
    closed: day === 0,
    open: day === 6 ? '08:00' : '06:00',
    close: day === 6 ? '14:00' : '21:00',
  }));
}

/** Filas del formulario a partir de lo guardado; si no hay nada guardado, el horario sugerido. */
export function rowsFromServer(hours: readonly DayHours[] | undefined): HoursRow[] {
  if (!hours || hours.length === 0) {
    return defaultHours();
  }

  return DAY_ORDER.map((day) => {
    const saved = hours.find((h) => h.day === day);
    return saved
      ? { day, closed: saved.closed, open: saved.open ?? '06:00', close: saved.close ?? '21:00' }
      : { day, closed: true, open: '06:00', close: '21:00' };
  });
}

/** Lo que se envía al servidor: los días cerrados no llevan horas. */
export function rowsToRequest(rows: readonly HoursRow[]): DayHours[] {
  return rows.map((r) => (r.closed ? { day: r.day, closed: true, open: null, close: null } : { day: r.day, closed: false, open: r.open, close: r.close }));
}

/** Primer problema del horario (apertura debe ser anterior al cierre en los días abiertos) o nulo. El servidor repite la validación. */
export function hoursProblem(rows: readonly HoursRow[]): { day: number } | null {
  const bad = rows.find((r) => !r.closed && (!/^([01]\d|2[0-3]):[0-5]\d$/.test(r.open) || !/^([01]\d|2[0-3]):[0-5]\d$/.test(r.close) || r.open >= r.close));
  return bad ? { day: bad.day } : null;
}
