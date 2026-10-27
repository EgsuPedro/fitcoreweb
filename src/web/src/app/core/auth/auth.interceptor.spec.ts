import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import type { TokenResponse } from '../api/models';
import { authInterceptor } from './auth.interceptor';
import { SessionService } from './session.service';

const tokens = (accessToken: string): TokenResponse => ({
  accessToken,
  tokenType: 'Bearer',
  expiresIn: 900,
  refreshToken: null,
  refreshTokenExpiresAt: '2030-01-01T00:00:00Z',
});

describe('authInterceptor', () => {
  let http: HttpClient;
  let backend: HttpTestingController;
  let session: SessionService;
  let navigate: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    navigate = vi.fn().mockResolvedValue(true);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: Router, useValue: { navigate, url: '/branches' } },
      ],
    });
    http = TestBed.inject(HttpClient);
    backend = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
  });

  afterEach(() => backend.verify());

  async function signIn(token: string): Promise<void> {
    const refresh = firstValueFrom(session.refresh());
    backend.expectOne('/api/v1/auth/refresh').flush(tokens(token));
    await refresh;
  }

  it('adjunta el token a las llamadas a la API', async () => {
    await signIn('t-1');

    const done = firstValueFrom(http.get('/api/v1/branches'));
    const request = backend.expectOne('/api/v1/branches');
    expect(request.request.headers.get('Authorization')).toBe('Bearer t-1');
    request.flush({});
    await done;
  });

  it('no adjunta el token a login ni a llamadas fuera de la API', async () => {
    await signIn('t-1');

    const login = firstValueFrom(http.post('/api/v1/auth/login', {}));
    const loginRequest = backend.expectOne('/api/v1/auth/login');
    expect(loginRequest.request.headers.has('Authorization')).toBe(false);
    loginRequest.flush({});
    await login;

    const external = firstValueFrom(http.get('https://otro-sitio.test/datos'));
    const externalRequest = backend.expectOne('https://otro-sitio.test/datos');
    expect(externalRequest.request.headers.has('Authorization')).toBe(false);
    externalRequest.flush({});
    await external;
  });

  it('ante un 401 renueva el token una vez y repite la petición', async () => {
    await signIn('t-viejo');

    const done = firstValueFrom(http.get<{ ok: boolean }>('/api/v1/branches'));
    backend.expectOne('/api/v1/branches').flush({}, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/v1/auth/refresh').flush(tokens('t-nuevo'));

    const retry = backend.expectOne('/api/v1/branches');
    expect(retry.request.headers.get('Authorization')).toBe('Bearer t-nuevo');
    retry.flush({ ok: true });

    expect(await done).toEqual({ ok: true });
    expect(navigate).not.toHaveBeenCalled();
  });

  it('varias peticiones con 401 a la vez provocan un solo refresh', async () => {
    await signIn('t-viejo');

    const a = firstValueFrom(http.get('/api/v1/branches'));
    const b = firstValueFrom(http.get('/api/v1/users'));
    backend.expectOne('/api/v1/branches').flush({}, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/v1/users').flush({}, { status: 401, statusText: 'Unauthorized' });

    backend.expectOne('/api/v1/auth/refresh').flush(tokens('t-nuevo'));
    backend.expectOne('/api/v1/branches').flush({});
    backend.expectOne('/api/v1/users').flush({});
    await Promise.all([a, b]);
  });

  it('si el refresh falla cierra la sesión y manda al login con el motivo', async () => {
    await signIn('t-viejo');

    const done = firstValueFrom(http.get('/api/v1/branches')).catch((error: { status: number }) => error.status);
    backend.expectOne('/api/v1/branches').flush({}, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/v1/auth/refresh').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(await done).toBe(401);
    expect(session.status()).toBe('anonymous');
    expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { returnUrl: '/branches', reason: 'expired' } });
  });

  it('si tras renovar el token sigue el 401 (usuario desactivado) cierra la sesión sin bucle', async () => {
    await signIn('t-viejo');

    const done = firstValueFrom(http.get('/api/v1/branches')).catch((error: { status: number }) => error.status);
    backend.expectOne('/api/v1/branches').flush({}, { status: 401, statusText: 'Unauthorized' });
    backend.expectOne('/api/v1/auth/refresh').flush(tokens('t-nuevo'));
    backend.expectOne('/api/v1/branches').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(await done).toBe(401);
    expect(session.status()).toBe('anonymous');
    expect(navigate).toHaveBeenCalledTimes(1);
  });

  it('los errores que no son 401 (403, 422…) se propagan sin tocar la sesión', async () => {
    await signIn('t-1');

    const done = firstValueFrom(http.get('/api/v1/users')).catch((error: { status: number }) => error.status);
    backend.expectOne('/api/v1/users').flush({ code: 'auth.forbidden' }, { status: 403, statusText: 'Forbidden' });

    expect(await done).toBe(403);
    expect(session.token()).toBe('t-1');
    expect(navigate).not.toHaveBeenCalled();
  });
});
