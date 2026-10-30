import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';
import type { ProblemDetails } from '../api/models';

/** Convierte errores HTTP de la API en mensajes en el idioma activo usando el `code` estable de negocio. */
@Injectable({ providedIn: 'root' })
export class ErrorMessages {
  private readonly transloco = inject(TranslocoService);

  message(error: unknown): string {
    const problem = this.problemOf(error);

    if (problem?.code) {
      const key = `errors.${problem.code}`;
      const text = this.transloco.translate(key);
      if (text !== key) {
        return text;
      }
    }

    if (problem?.errors) {
      const details = Object.values(problem.errors).flat();
      if (details.length > 0) {
        return details.join(' ');
      }
    }

    // Solo un error HTTP tiene estado (0 = sin conexión); cualquier otra excepción es un error genérico.
    if (!(error instanceof HttpErrorResponse)) {
      return this.transloco.translate('errors.generic');
    }

    const statusKey = `errors.http_${error.status}`;
    const statusText = this.transloco.translate(statusKey);
    return statusText !== statusKey ? statusText : this.transloco.translate('errors.generic');
  }

  /** Texto de interfaz en el idioma activo, para mensajes de validación propios de una pantalla. */
  text(key: string, params?: Record<string, unknown>): string {
    return this.transloco.translate(key, params);
  }

  /** Código estable de negocio del error (por ejemplo «registration.slug_taken»), o nulo si no lo trae. */
  code(error: unknown): string | null {
    return this.problemOf(error)?.code ?? null;
  }

  problemOf(error: unknown): ProblemDetails | null {
    if (error instanceof HttpErrorResponse && error.error && typeof error.error === 'object') {
      return error.error as ProblemDetails;
    }
    return null;
  }
}

/** Fechas en el idioma activo (es-EC / en). */
@Injectable({ providedIn: 'root' })
export class Formatter {
  private readonly transloco = inject(TranslocoService);

  private get locale(): string {
    return this.transloco.getActiveLang() === 'en' ? 'en-US' : 'es-EC';
  }

  /** Dólares con el formato del idioma activo. Solo formatea: los cálculos monetarios los hace siempre el backend. */
  money(amount: number): string {
    return new Intl.NumberFormat(this.locale, { style: 'currency', currency: 'USD' }).format(amount);
  }

  /** Número con el separador decimal del idioma activo (29,4 en español, 29.4 en inglés). */
  number(value: number, maxDecimals = 2): string {
    return new Intl.NumberFormat(this.locale, { maximumFractionDigits: maxDecimals }).format(value);
  }

  /** Fecha sin hora (`2026-11-26`) tal cual, sin el corrimiento de zona horaria que sufre `new Date('2026-11-26')`. */
  dateOnly(value: string): string {
    const [year, month, day] = value.slice(0, 10).split('-').map(Number);
    if (!year || !month || !day) {
      return value;
    }
    return new Intl.DateTimeFormat(this.locale, { dateStyle: 'medium' }).format(new Date(year, month - 1, day));
  }

  time(iso: string): string {
    return new Intl.DateTimeFormat(this.locale, { timeStyle: 'short' }).format(new Date(iso));
  }
  dateTime(iso: string): string {
    const locale = this.transloco.getActiveLang() === 'en' ? 'en-US' : 'es-EC';
    return new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso));
  }

  /** Fecha y hora en la zona de la sucursal (las sesiones se muestran siempre en su hora, no en la del navegador). */
  dateTimeIn(iso: string, timeZone: string): string {
    return new Intl.DateTimeFormat(this.locale, { dateStyle: 'medium', timeStyle: 'short', timeZone }).format(new Date(iso));
  }

  timeIn(iso: string, timeZone: string): string {
    return new Intl.DateTimeFormat(this.locale, { timeStyle: 'short', timeZone }).format(new Date(iso));
  }

  /** «lunes 2 de noviembre» para una fecha local `YYYY-MM-DD`. */
  dayHeading(dateKey: string): string {
    const [year, month, day] = dateKey.split('-').map(Number);
    return new Intl.DateTimeFormat(this.locale, { weekday: 'long', day: 'numeric', month: 'long', timeZone: 'UTC' }).format(new Date(Date.UTC(year, month - 1, day)));
  }

  /** «lun» y «2» para las cabeceras de una semana. */
  weekdayShort(dateKey: string): string {
    const [year, month, day] = dateKey.split('-').map(Number);
    return new Intl.DateTimeFormat(this.locale, { weekday: 'short', timeZone: 'UTC' }).format(new Date(Date.UTC(year, month - 1, day)));
  }

  monthTitle(dateKey: string): string {
    const [year, month, day] = dateKey.split('-').map(Number);
    return new Intl.DateTimeFormat(this.locale, { month: 'long', year: 'numeric', timeZone: 'UTC' }).format(new Date(Date.UTC(year, month - 1, day)));
  }

  date(iso: string): string {
    const locale = this.transloco.getActiveLang() === 'en' ? 'en-US' : 'es-EC';
    return new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(new Date(iso));
  }
}
