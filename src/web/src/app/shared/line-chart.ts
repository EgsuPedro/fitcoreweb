import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { Formatter } from '../core/i18n/messages';
import { ChartPoint, layoutSeries } from './chart-math';

const WIDTH = 520;
const HEIGHT = 200;
const PAD = 28;

/**
 * Gráfico de líneas en SVG propio (sin librerías). Es una ayuda visual del progreso: no interpreta ni diagnostica. Cada punto
 * lleva su valor como texto (tooltip nativo) y una tabla equivalente queda para lectores de pantalla.
 */
@Component({
  selector: 'fc-line-chart',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (layout().points.length === 0) {
      <p class="muted">{{ 'wellness.chart.noData' | transloco }}</p>
    } @else {
      <svg class="line-chart" [attr.viewBox]="'0 0 ' + w + ' ' + h" role="img" [attr.aria-label]="label()" preserveAspectRatio="xMidYMid meet">
        <line class="chart-axis" [attr.x1]="pad" [attr.y1]="h - pad" [attr.x2]="w - pad" [attr.y2]="h - pad" />
        <line class="chart-axis" [attr.x1]="pad" [attr.y1]="pad" [attr.x2]="pad" [attr.y2]="h - pad" />
        <text class="chart-label" [attr.x]="pad + 4" [attr.y]="pad - 8">{{ format(layout().max) }}</text>
        <text class="chart-label" [attr.x]="pad + 4" [attr.y]="h - pad + 16">{{ format(layout().min) }} {{ unit() }}</text>
        @if (layout().points.length > 1) {
          <polyline class="chart-line" fill="none" [attr.points]="layout().line" />
        }
        @for (p of layout().points; track $index) {
          <circle class="chart-dot" [attr.cx]="p.x" [attr.cy]="p.y" r="4"><title>{{ labelOf(p.label) }}: {{ format(p.value) }} {{ unit() }}</title></circle>
        }
        <text class="chart-label" text-anchor="start" [attr.x]="pad" [attr.y]="h - 4">{{ labelOf(first().label) }}</text>
        @if (layout().points.length > 1) {
          <text class="chart-label" text-anchor="end" [attr.x]="w - pad" [attr.y]="h - 4">{{ labelOf(last().label) }}</text>
        }
      </svg>
      <table class="sr-only">
        <caption>{{ label() }}</caption>
        <tbody>
          @for (p of layout().points; track $index) {
            <tr><th scope="row">{{ labelOf(p.label) }}</th><td>{{ format(p.value) }} {{ unit() }}</td></tr>
          }
        </tbody>
      </table>
    }
  `,
})
export class LineChart {
  private readonly formatter = inject(Formatter);

  readonly points = input.required<readonly ChartPoint[]>();
  readonly label = input.required<string>();
  readonly unit = input('');
  readonly decimals = input(1);

  protected readonly w = WIDTH;
  protected readonly h = HEIGHT;
  protected readonly pad = PAD;
  protected readonly layout = computed(() => layoutSeries(this.points(), WIDTH, HEIGHT, PAD));
  protected readonly first = computed(() => this.layout().points[0]);
  protected readonly last = computed(() => this.layout().points[this.layout().points.length - 1]);

  /** Las fechas ISO (yyyy-MM-dd) se muestran en el idioma activo; cualquier otra etiqueta, tal cual. */
  protected labelOf(label: string): string {
    return /^d{4}-d{2}-d{2}$/.test(label) ? this.formatter.dateOnly(label) : label;
  }

  protected format(value: number): string {
    return this.formatter.number(value, this.decimals());
  }
}
