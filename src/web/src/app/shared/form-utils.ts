/** Número de un campo de texto; vacío o inválido es nulo (el servidor valida rangos). */
export function toNumber(value: string): number | null {
  const parsed = Number(value.trim().replace(',', '.'));
  return value.trim() === '' || Number.isNaN(parsed) ? null : parsed;
}

/** Entero de un campo de texto; vacío o inválido es nulo. */
export function toInt(value: string): number | null {
  const parsed = toNumber(value);
  return parsed === null ? null : Math.trunc(parsed);
}

/** Fecha de hoy como yyyy-MM-dd en la zona del navegador (el servidor valida contra el día de la sucursal). */
export function todayIso(): string {
  const now = new Date();
  const pad = (n: number): string => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/** Líneas no vacías de un cuadro de texto (una por línea). */
export function toLines(value: string): string[] {
  return value
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}
