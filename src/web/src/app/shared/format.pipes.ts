import { Pipe, PipeTransform, inject } from '@angular/core';
import { Formatter } from '../core/i18n/messages';

/** Dinero en dólares (impuro: debe reaccionar al cambio de idioma). */
@Pipe({ name: 'fcMoney', pure: false })
export class FcMoney implements PipeTransform {
  private readonly format = inject(Formatter);

  transform(value: number | null | undefined): string {
    return value === null || value === undefined ? '—' : this.format.money(value);
  }
}

/** Fecha sin hora (campos DateOnly de la API). */
@Pipe({ name: 'fcDate', pure: false })
export class FcDate implements PipeTransform {
  private readonly format = inject(Formatter);

  transform(value: string | null | undefined): string {
    return value ? this.format.dateOnly(value) : '—';
  }
}

/** Fecha y hora de un instante (campos con hora de la API). */
@Pipe({ name: 'fcDateTime', pure: false })
export class FcDateTime implements PipeTransform {
  private readonly format = inject(Formatter);

  transform(value: string | null | undefined): string {
    return value ? this.format.dateTime(value) : '—';
  }
}