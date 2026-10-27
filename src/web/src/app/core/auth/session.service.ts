import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, catchError, finalize, firstValueFrom, map, of, shareReplay, switchMap, tap } from 'rxjs';
import { API_BASE } from '../api/api.service';
import type { Me, TokenResponse } from '../api/models';

export type SessionStatus = 'unknown' | 'authenticated' | 'anonymous';

export interface LoginInput {
  tenantSlug: string;
  email: string;
  password: string;
}

/**
 * Sesión del usuario. El access token vive SOLO en memoria (nunca en localStorage); el refresh token viaja en
 * una cookie HttpOnly que el navegador envía a /api/v1/auth, por lo que el JavaScript no puede leerlo.
 * Al recargar la página se recupera la sesión pidiendo un access token nuevo con esa cookie.
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly http = inject(HttpClient);

  private accessToken: string | null = null;
  private refreshInFlight: Observable<boolean> | null = null;
  private restoring: Promise<void> | null = null;

  readonly status = signal<SessionStatus>('unknown');
  readonly user = signal<Me | null>(null);
  readonly isAuthenticated = computed(() => this.status() === 'authenticated');
  readonly permissions = computed(() => new Set(this.user()?.permissions ?? []));
  readonly allBranches = computed(() => this.user()?.allBranches ?? false);

  token(): string | null {
    return this.accessToken;
  }

  has(permission: string): boolean {
    return this.permissions().has(permission);
  }

  login(input: LoginInput): Observable<Me> {
    return this.http
      .post<TokenResponse>(`${API_BASE}/auth/login`, { ...input, useCookie: true })
      .pipe(
        tap((tokens) => (this.accessToken = tokens.accessToken)),
        switchMap(() => this.loadProfile()),
      );
  }

  /** Recupera la sesión al arrancar (una sola vez). Nunca lanza: si no hay sesión, queda anónimo. */
  restore(): Promise<void> {
    if (this.status() !== 'unknown') {
      return Promise.resolve();
    }

    this.restoring ??= firstValueFrom(
      this.refresh().pipe(
        switchMap((ok) => (ok ? this.loadProfile() : of(null))),
        catchError(() => of(null)),
        map(() => undefined),
      ),
    ).then(() => {
      if (this.status() === 'unknown') {
        this.status.set('anonymous');
      }
    });
    return this.restoring;
  }

  /** Pide un access token nuevo. Varias llamadas simultáneas comparten una sola petición. */
  refresh(): Observable<boolean> {
    this.refreshInFlight ??= this.http.post<TokenResponse>(`${API_BASE}/auth/refresh`, {}).pipe(
      tap((tokens) => (this.accessToken = tokens.accessToken)),
      map(() => true),
      catchError(() => {
        this.clear();
        return of(false);
      }),
      finalize(() => (this.refreshInFlight = null)),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    return this.refreshInFlight;
  }

  logout(): Observable<void> {
    return this.http.post<void>(`${API_BASE}/auth/logout`, {}).pipe(
      catchError(() => of(undefined)),
      tap(() => this.clear()),
      map(() => undefined),
    );
  }

  /** Cierra la sesión localmente (token vencido o revocado). */
  clear(): void {
    this.accessToken = null;
    this.user.set(null);
    this.status.set('anonymous');
  }

  private loadProfile(): Observable<Me> {
    // El interceptor adjunta el token que acaba de guardarse.
    return this.http.get<Me>(`${API_BASE}/auth/me`).pipe(
      tap((me) => {
        this.user.set(me);
        this.status.set('authenticated');
      }),
    );
  }
}
