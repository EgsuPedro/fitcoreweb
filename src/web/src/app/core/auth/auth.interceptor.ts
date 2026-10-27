import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { API_BASE } from '../api/api.service';
import { SessionService } from './session.service';

const PUBLIC_AUTH_PATHS = ['/auth/login', '/auth/refresh', '/auth/logout'];

function isApiCall(url: string): boolean {
  return url.startsWith(API_BASE);
}

function isPublicAuthCall(url: string): boolean {
  return PUBLIC_AUTH_PATHS.some((path) => url.startsWith(`${API_BASE}${path}`));
}

/**
 * Adjunta el access token a las llamadas a la API. Si la API responde 401 (token vencido, usuario desactivado o
 * gimnasio suspendido) intenta UN refresh y repite la petición; si no es posible, cierra la sesión y vuelve al login.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const session = inject(SessionService);
  const router = inject(Router);

  if (!isApiCall(request.url) || isPublicAuthCall(request.url)) {
    return next(request);
  }

  const withToken = (req: HttpRequest<unknown>): HttpRequest<unknown> => {
    const token = session.token();
    return token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  };

  const expire = (): void => {
    session.clear();
    void router.navigate(['/login'], { queryParams: { returnUrl: router.url, reason: 'expired' } });
  };

  return next(withToken(request)).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401) {
        return throwError(() => error);
      }

      return session.refresh().pipe(
        switchMap((refreshed) => {
          if (!refreshed) {
            expire();
            return throwError(() => error);
          }

          return next(withToken(request)).pipe(
            catchError((retryError: unknown) => {
              if (retryError instanceof HttpErrorResponse && retryError.status === 401) {
                expire();
              }
              return throwError(() => retryError);
            }),
          );
        }),
      );
    }),
  );
};
