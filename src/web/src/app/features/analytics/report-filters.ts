import { ChangeDetectionStrategy, Component, DestroyRef, Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { EMPTY, Subject, catchError, switchMap } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { AnalyticsContext } from '../../core/api/models';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { FcDate } from '../../shared/format.pipes';
import { PRESETS, type Preset, rangeError } from './analytics-math';

export type ReportQuery = Record<string, string | readonly string[]>;

/**
 * Los filtros compartidos de todas las pantallas de analítica (período, rango y sucursales): se eligen una vez y valen en todos los reportes.
 * Solo se arma una consulta si los filtros son válidos; los límites reales los valida el servidor.
 */
@Injectable({ providedIn: 'root' })
export class ReportFilters {
  private readonly session = inject(SessionService);

  readonly preset = signal<Preset>('Last30Days');
  readonly from = signal('');
  readonly to = signal('');
  readonly branchIds = signal<string[]>([]);

  /** Lo que el servidor resolvió: período aplicado y comparable, zona horaria, sucursales y áreas permitidas. */
  readonly context = signal<AnalyticsContext | null>(null);
  readonly contextError = signal<string | null>(null);

  readonly error = computed(() => rangeError(this.preset(), this.from(), this.to()));

  /** Consulta lista para la API, o nula si los filtros no son válidos. */
  readonly query = computed<ReportQuery | null>(() => {
    if (this.error()) {
      return null;
    }
    const query: ReportQuery = { preset: this.preset() };
    if (this.preset() === 'Custom') {
      query['from'] = this.from();
      query['to'] = this.to();
    }
    if (this.branchIds().length > 0) {
      query['branchId'] = this.branchIds();
    }
    return query;
  });

  constructor() {
    // Al cambiar de usuario no se arrastran sucursales ni contexto del anterior.
    effect(() => {
      this.session.user();
      untracked(() => {
        this.context.set(null);
        this.branchIds.set([]);
      });
    });
  }

  toggleBranch(id: string, on: boolean): void {
    this.branchIds.update((ids) => (on ? [...new Set([...ids, id])] : ids.filter((x) => x !== id)));
  }

  /** Las áreas (finanzas, alumnos, operación…) que el servidor autoriza a este usuario. */
  sections = computed(() => this.context()?.sections ?? null);
}

/** La barra de filtros: período, fechas, sucursales, y el período que el servidor aplicó (con su comparable y la zona horaria). */
@Component({
  selector: 'fc-report-filters',
  imports: [TranslocoPipe, FcDate],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="filters card" role="search" [attr.aria-label]="'analytics.filters.title' | transloco" (submit)="$event.preventDefault()">
      <div class="filters-row">
        <label class="inline-field">
          <span>{{ 'analytics.filters.period' | transloco }}</span>
          <select [value]="filters.preset()" (change)="filters.preset.set($any($event.target).value)">
            @for (p of presets; track p) {
              <option [value]="p">{{ 'analytics.presets.' + p | transloco }}</option>
            }
          </select>
        </label>
        @if (filters.preset() === 'Custom') {
          <label class="inline-field">
            <span>{{ 'common.from' | transloco }}</span>
            <input type="date" [value]="filters.from()" (change)="filters.from.set($any($event.target).value)" />
          </label>
          <label class="inline-field">
            <span>{{ 'common.to' | transloco }}</span>
            <input type="date" [value]="filters.to()" (change)="filters.to.set($any($event.target).value)" />
          </label>
        }
      </div>

      @if (branches().length > 1) {
        <fieldset class="branch-filter">
          <legend>{{ 'analytics.filters.branches' | transloco }}</legend>
          @for (b of branches(); track b.id) {
            <label class="check">
              <input type="checkbox" [checked]="filters.branchIds().includes(b.id)" (change)="filters.toggleBranch(b.id, $any($event.target).checked)" />
              <span>{{ b.name }}</span>
            </label>
          }
          <small class="muted">{{ 'analytics.filters.branchesHint' | transloco }}</small>
        </fieldset>
      }

      @if (filters.error(); as key) {
        <p class="alert alert-danger" role="alert">{{ key | transloco }}</p>
      } @else if (filters.contextError(); as message) {
        <p class="alert alert-danger" role="alert">{{ message }}</p>
      } @else if (filters.context(); as c) {
        <p class="muted applied" aria-live="polite">
          {{ 'analytics.filters.applied' | transloco: { days: c.period.days } }}
          <strong>{{ c.period.from | fcDate }} → {{ c.period.to | fcDate }}</strong>
          · {{ 'analytics.filters.compared' | transloco }} {{ c.comparable.from | fcDate }} → {{ c.comparable.to | fcDate }}
          · {{ 'analytics.filters.timeZone' | transloco }}: {{ c.timeZone }} · {{ c.currency }}
        </p>
      }
    </form>
  `,
})
export class ReportFilterBar {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  protected readonly filters = inject(ReportFilters);
  protected readonly presets = PRESETS;
  protected readonly branches = computed(() => this.filters.context()?.branches ?? []);

  private readonly refresh = new Subject<ReportQuery>();

  private readonly destroyRef = inject(DestroyRef);

  constructor() {
    this.refresh
      .pipe(
        switchMap((query) =>
          this.api.analyticsContext(query).pipe(
            catchError((err: unknown) => {
              this.filters.contextError.set(this.errors.message(err));
              return EMPTY;
            }),
          ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((context) => {
        this.filters.contextError.set(null);
        this.filters.context.set(context);
      });

    // Cada cambio válido de filtros vuelve a pedir el contexto: el servidor es quien fija «hoy», la zona horaria y el período comparable.
    effect(() => {
      const query = this.filters.query();
      if (query) {
        untracked(() => this.refresh.next(query));
      }
    });
  }
}
