import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { Formatter } from '../../core/i18n/messages';
import { layoutPair } from './analytics-math';

const WIDTH = 560;
const HEIGHT = 200;
const PAD = 30;

export interface PairSeries {
  starts: readonly string[];
  values: readonly number[];
}

/**
 * Gráfico de dos líneas en SVG propio: el período (línea continua) y su comparable (línea punteada) en la misma escala y con el eje en cero. Lleva
 * título, unidad, leyenda, etiquetas de los extremos, el valor de cada punto como texto y una tabla equivalente para lectores de pantalla.
 */
@Component({
  selector: 'fc-pair-chart',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <figure class="pair-chart">
      <figcaption>
        <strong>{{ title() }}</strong>
        <span class="legend">
          <span class="legend-current" aria-hidden="true">━</span> {{ 'analytics.chart.current' | transloco }}
          <span class="legend-previous" aria-hidden="true">┅</span> {{ 'analytics.chart.previous' | transloco }}
        </span>
      </figcaption>
      @if (current().values.length === 0) {
        <p class="muted">{{ 'analytics.noData' | transloco }}</p>
      } @else {
        <svg class="line-chart" [attr.viewBox]="'0 0 ' + w + ' ' + h" role="img" [attr.aria-label]="title()" preserveAspectRatio="xMidYMid meet">
          <line class="chart-axis" [attr.x1]="pad" [attr.y1]="h - pad" [attr.x2]="w - pad" [attr.y2]="h - pad" />
          <line class="chart-axis" [attr.x1]="pad" [attr.y1]="pad" [attr.x2]="pad" [attr.y2]="h - pad" />
          <text class="chart-label" [attr.x]="pad + 4" [attr.y]="pad - 10">{{ valueText(layout().max) }}</text>
          <text class="chart-label" [attr.x]="pad + 4" [attr.y]="h - pad + 14">{{ valueText(layout().min) }}</text>
          @if (layout().previous.length > 1) {
            <polyline class="chart-line chart-line-previous" fill="none" [attr.points]="layout().previousLine" />
          }
          @if (layout().current.length > 1) {
            <polyline class="chart-line" fill="none" [attr.points]="layout().currentLine" />
          }
          @for (p of layout().current; track $index) {
            <circle class="chart-dot" [attr.cx]="p.x" [attr.cy]="p.y" r="3.5"><title>{{ dateText(current().starts[$index]) }}: {{ valueText(current().values[$index]) }}</title></circle>
          }
          @for (p of layout().previous; track $index) {
            <circle class="chart-dot chart-dot-previous" [attr.cx]="p.x" [attr.cy]="p.y" r="3"><title>{{ dateText(previous().starts[$index]) }}: {{ valueText(previous().values[$index]) }}</title></circle>
          }
          <text class="chart-label" text-anchor="start" [attr.x]="pad" [attr.y]="h - 4">{{ dateText(current().starts[0]) }}</text>
          @if (current().starts.length > 1) {
            <text class="chart-label" text-anchor="end" [attr.x]="w - pad" [attr.y]="h - 4">{{ dateText(current().starts[current().starts.length - 1]) }}</text>
          }
        </svg>
        <table class="sr-only">
          <caption>{{ title() }}</caption>
          <thead><tr><th scope="col">{{ 'analytics.chart.date' | transloco }}</th><th scope="col">{{ 'analytics.chart.current' | transloco }}</th><th scope="col">{{ 'analytics.chart.previous' | transloco }}</th></tr></thead>
          <tbody>
            @for (start of current().starts; track $index) {
              <tr><th scope="row">{{ dateText(start) }}</th><td>{{ valueText(current().values[$index]) }}</td><td>{{ previous().values[$index] === undefined ? '—' : valueText(previous().values[$index]) }}</td></tr>
            }
          </tbody>
        </table>
      }
    </figure>
  `,
})
export class PairChart {
  private readonly format = inject(Formatter);

  readonly title = input.required<string>();
  readonly current = input.required<PairSeries>();
  readonly previous = input.required<PairSeries>();
  /** Los importes se muestran como dólares; las cantidades, como números enteros. */
  readonly money = input(false);

  protected readonly w = WIDTH;
  protected readonly h = HEIGHT;
  protected readonly pad = PAD;
  protected readonly layout = computed(() => layoutPair(this.current().values, this.previous().values, WIDTH, HEIGHT, PAD));

  protected valueText(value: number): string {
    return this.money() ? this.format.money(value) : this.format.number(value, 0);
  }

  protected dateText(start: string | undefined): string {
    return start ? this.format.dateOnly(start) : '';
  }
}
