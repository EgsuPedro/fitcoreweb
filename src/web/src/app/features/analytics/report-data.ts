import { DestroyRef, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { EMPTY, Observable, Subject, catchError, switchMap } from 'rxjs';
import type { ErrorMessages } from '../../core/i18n/messages';

type Params = Record<string, string | number | boolean | null | undefined | readonly string[]>;

/**
 * Carga de un reporte: dato, «cargando» y error. Una consulta nueva descarta la anterior (`switchMap`), así que cambiar un filtro rápido nunca
 * muestra el resultado de una consulta vieja. Se crea en el constructor del componente.
 */
export class ReportData<T> {
  readonly data = signal<T | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  private readonly trigger = new Subject<Params>();

  constructor(fetch: (query: Params) => Observable<T>, errors: ErrorMessages, destroyRef: DestroyRef) {
    this.trigger
      .pipe(
        switchMap((query) => {
          this.loading.set(true);
          this.error.set(null);
          return fetch(query).pipe(
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
        this.data.set(result);
        this.loading.set(false);
      });
  }

  load(query: Params): void {
    this.trigger.next(query);
  }
}
