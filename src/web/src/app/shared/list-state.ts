import { DestroyRef, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, Subject, catchError, debounceTime, switchMap } from 'rxjs';
import type { Paged } from '../core/api/models';
import type { ErrorMessages } from '../core/i18n/messages';

type Filters = Record<string, string | number | boolean | null | undefined>;

/**
 * Estado de un listado paginado con filtros y búsqueda: carga, error y página. Las respuestas viejas se descartan
 * (`switchMap`), así que cambiar un filtro rápido nunca muestra datos de una consulta anterior.
 * Se crea en el constructor del componente (necesita el contexto de inyección para limpiarse al destruirse).
 */
export class ListState<T> {
  readonly items = signal<T[]>([]);
  readonly total = signal(0);
  readonly page = signal(1);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  private filters: Filters = {};
  private readonly trigger = new Subject<void>();
  private readonly searchInput = new Subject<string>();

  constructor(
    private readonly fetch: (query: Filters) => Observable<Paged<T>>,
    errors: ErrorMessages,
    destroyRef: DestroyRef,
    readonly pageSize = 20,
    initial: Filters = {},
  ) {
    this.filters = { ...initial };
    this.trigger
      .pipe(
        switchMap(() => {
          this.loading.set(true);
          this.error.set(null);
          return this.fetch({ ...this.filters, page: this.page(), pageSize: this.pageSize }).pipe(
            catchError((err: unknown) => {
              this.loading.set(false);
              this.error.set(errors.message(err));
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe((result) => {
        this.items.set(result.items);
        this.total.set(result.totalCount);
        this.loading.set(false);
      });
    this.searchInput.pipe(debounceTime(300), takeUntilDestroyed(destroyRef)).subscribe((text) => this.set({ search: text }));
  }

  load(): void {
    this.trigger.next();
  }

  /** Cambia filtros y vuelve a la primera página. */
  set(changes: Filters): void {
    this.filters = { ...this.filters, ...changes };
    this.page.set(1);
    this.load();
  }

  search(text: string): void {
    this.searchInput.next(text);
  }

  goTo(page: number): void {
    this.page.set(page);
    this.load();
  }
}