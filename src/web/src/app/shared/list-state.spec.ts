import { DestroyRef } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject, of, throwError } from 'rxjs';
import type { Paged } from '../core/api/models';
import type { ErrorMessages } from '../core/i18n/messages';
import { ListState } from './list-state';

const errors = { message: () => 'falló' } as unknown as ErrorMessages;
const page = (items: number[]): Paged<number> => ({ items, totalCount: items.length, page: 1, pageSize: 20 }) as Paged<number>;

describe('ListState', () => {
  const destroyRef = () => TestBed.inject(DestroyRef);

  it('carga la primera página y publica total y elementos', () => {
    const fetch = vi.fn(() => of(page([1, 2, 3])));
    const state = TestBed.runInInjectionContext(() => new ListState<number>(fetch, errors, destroyRef()));
    state.load();
    expect(state.items()).toEqual([1, 2, 3]);
    expect(state.total()).toBe(3);
    expect(state.loading()).toBe(false);
    expect(fetch).toHaveBeenCalledWith({ page: 1, pageSize: 20 });
  });

  it('al cambiar un filtro vuelve a la página 1 y lo envía', () => {
    const fetch = vi.fn(() => of(page([])));
    const state = TestBed.runInInjectionContext(() => new ListState<number>(fetch, errors, destroyRef()));
    state.goTo(3);
    state.set({ status: 'Active' });
    expect(state.page()).toBe(1);
    expect(fetch).toHaveBeenLastCalledWith({ status: 'Active', page: 1, pageSize: 20 });
  });

  it('un error de la API se muestra y no deja la lista cargando', () => {
    const state = TestBed.runInInjectionContext(
      () => new ListState<number>(() => throwError(() => new Error('x')), errors, destroyRef()),
    );
    state.load();
    expect(state.error()).toBe('falló');
    expect(state.loading()).toBe(false);
  });

  it('descarta la respuesta vieja cuando llega otra consulta más nueva', () => {
    const first = new Subject<Paged<number>>();
    const second = new Subject<Paged<number>>();
    const responses = [first, second];
    const state = TestBed.runInInjectionContext(
      () => new ListState<number>(() => responses.shift()!.asObservable(), errors, destroyRef()),
    );
    state.load();
    state.set({ search: 'b' });
    first.next(page([1]));
    expect(state.items()).toEqual([]);
    second.next(page([2]));
    expect(state.items()).toEqual([2]);
  });
});
