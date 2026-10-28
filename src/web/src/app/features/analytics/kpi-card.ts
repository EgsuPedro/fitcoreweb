import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import type { Kpi } from '../../core/api/models';
import { Formatter } from '../../core/i18n/messages';
import { variationView, reportLink } from './analytics-math';

/**
 * Un indicador con todo lo necesario para leerlo sin ambigüedad: nombre y definición (traducidos), valor con su unidad, el del período comparable y la
 * variación, o el motivo por el que no hay (sin base, sin datos). El color nunca es lo único que informa: siempre hay texto y una flecha.
 */
@Component({
  selector: 'fc-kpi',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="kpi" [class.kpi-empty]="kpi().isEmpty" [attr.aria-label]="'analytics.kpi.' + kpi().key + '.name' | transloco">
      <header>
        <h3>{{ 'analytics.kpi.' + kpi().key + '.name' | transloco }}</h3>
        <details class="kpi-help">
          <summary [attr.aria-label]="'analytics.kpi.help' | transloco">ⓘ</summary>
          <p>{{ 'analytics.kpi.' + kpi().key + '.definition' | transloco }}</p>
        </details>
      </header>

      <p class="kpi-value">{{ valueText() }}</p>

      @if (kpi().isEmpty) {
        <p class="muted kpi-note">{{ 'analytics.empty' | transloco }}</p>
      } @else {
        @switch (view().kind) {
          @case ('change') {
            <p class="kpi-variation" [attr.aria-label]="'analytics.kpi.change' | transloco">
              <span aria-hidden="true">{{ arrow() }}</span> {{ changeText() }}
              <span class="muted">· {{ 'analytics.kpi.previous' | transloco }}: {{ previousText() }}</span>
            </p>
          }
          @case ('noBaseline') {
            <p class="muted kpi-note">{{ 'analytics.noBaseline' | transloco }}@if (kpi().previous !== null) { · {{ 'analytics.kpi.previous' | transloco }}: {{ previousText() }} }</p>
          }
          @case ('noData') {
            <p class="muted kpi-note">{{ 'analytics.noData' | transloco }}</p>
          }
          @default {
            <p class="muted kpi-note">{{ 'analytics.kpi.snapshot' | transloco }}</p>
          }
        }
      }

      @if (link(); as target) {
        <a class="kpi-link" [routerLink]="target.path" [queryParams]="{ tab: target.tab }">{{ 'analytics.viewDetail' | transloco }}</a>
      }
    </article>
  `,
})
export class KpiCard {
  private readonly format = inject(Formatter);

  readonly kpi = input.required<Kpi>();

  protected readonly view = computed(() => variationView(this.kpi()));
  protected readonly link = computed(() => reportLink(this.kpi().report));

  // Métodos y no computed: el formato depende del idioma activo, que no es una señal.
  protected valueText(): string {
    return this.render(this.kpi().value);
  }

  protected previousText(): string {
    return this.render(this.kpi().previous);
  }

  protected readonly arrow = computed(() => {
    const v = this.view();
    return v.kind === 'change' ? (v.direction === 'up' ? '▲' : v.direction === 'down' ? '▼' : '■') : '';
  });

  protected changeText(): string {
    const v = this.view();
    if (v.kind !== 'change') {
      return '';
    }
    const sign = v.value > 0 ? '+' : '';
    return v.unit === 'points' ? `${sign}${this.format.number(v.value, 1)} pts` : `${sign}${this.format.number(v.value, 1)} %`;
  }

  private render(value: number | null): string {
    if (value === null || value === undefined) {
      return '—';
    }
    switch (this.kpi().unit) {
      case 'Currency':
        return this.format.money(value);
      case 'Percent':
        return `${this.format.number(value, 1)} %`;
      case 'Average':
        return this.format.number(value, 2);
      default:
        return this.format.number(value, 0);
    }
  }
}
