import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import type { Me, TokenResponse } from '../api/models';
import { SessionService } from './session.service';

const tokens: TokenResponse = {
  accessToken: 'access-1',
  tokenType: 'Bearer',
  expiresIn: 900,
  refreshToken: null,
  refreshTokenExpiresAt: '2030-01-01T00:00:00Z',
};

const me: Me = {
  userId: 'u1',
  tenantId: 't1',
  tenantSlug: 'mi-gym',
  email: 'dueno@mi-gym.test',
  fullName: 'Dueño',
  roles: ['Propietario'],
  permissions: ['branches.read', 'users.read'],
  branchIds: [],
  allBranches: true,
  mustChangePassword: false,
};

describe('SessionService', () => {
  let session: SessionService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    session = TestBed.inject(SessionService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('login pide el refresh en cookie, guarda el token en memoria y carga el perfil', async () => {
    const done = firstValueFrom(session.login({ tenantSlug: 'mi-gym', email: 'a@b.test', password: 'x' }));

    const login = http.expectOne('/api/v1/auth/login');
    expect(login.request.body).toEqual({ tenantSlug: 'mi-gym', email: 'a@b.test', password: 'x', useCookie: true });
    login.flush(tokens);
    http.expectOne('/api/v1/auth/me').flush(me);
    await done;

    expect(session.token()).toBe('access-1');
    expect(session.isAuthenticated()).toBe(true);
    expect(session.has('branches.read')).toBe(true);
    expect(session.has('roles.manage')).toBe(false);
    expect(session.allBranches()).toBe(true);
  });

  it('el token no se guarda en localStorage ni sessionStorage', async () => {
    const done = firstValueFrom(session.login({ tenantSlug: 'mi-gym', email: 'a@b.test', password: 'x' }));
    http.expectOne('/api/v1/auth/login').flush(tokens);
    http.expectOne('/api/v1/auth/me').flush(me);
    await done;

    const stored = JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage });
    expect(stored).not.toContain('access-1');
  });

  it('restore recupera la sesión con la cookie de refresh', async () => {
    const done = session.restore();

    http.expectOne('/api/v1/auth/refresh').flush(tokens);
    http.expectOne('/api/v1/auth/me').flush(me);
    await done;

    expect(session.status()).toBe('authenticated');
    expect(session.user()?.tenantSlug).toBe('mi-gym');
  });

  it('restore sin sesión previa deja el estado anónimo y no lanza', async () => {
    const done = session.restore();

    http.expectOne('/api/v1/auth/refresh').flush({ code: 'auth.invalid_refresh' }, { status: 401, statusText: 'Unauthorized' });
    await done;

    expect(session.status()).toBe('anonymous');
    expect(session.token()).toBeNull();
  });

  it('restore se ejecuta una sola vez aunque varios guards lo pidan a la vez', async () => {
    const first = session.restore();
    const second = session.restore();

    http.expectOne('/api/v1/auth/refresh').flush(tokens);
    http.expectOne('/api/v1/auth/me').flush(me);
    await Promise.all([first, second]);

    expect(session.status()).toBe('authenticated');
  });

  it('varios refresh simultáneos comparten una sola petición', async () => {
    const results = Promise.all([firstValueFrom(session.refresh()), firstValueFrom(session.refresh()), firstValueFrom(session.refresh())]);

    http.expectOne('/api/v1/auth/refresh').flush(tokens);

    expect(await results).toEqual([true, true, true]);
    expect(session.token()).toBe('access-1');
  });

  it('un refresh fallido cierra la sesión local', async () => {
    const done = firstValueFrom(session.refresh());

    http.expectOne('/api/v1/auth/refresh').flush({}, { status: 401, statusText: 'Unauthorized' });

    expect(await done).toBe(false);
    expect(session.status()).toBe('anonymous');
  });

  it('logout limpia la sesión aunque el servidor falle', async () => {
    const login = firstValueFrom(session.login({ tenantSlug: 'mi-gym', email: 'a@b.test', password: 'x' }));
    http.expectOne('/api/v1/auth/login').flush(tokens);
    http.expectOne('/api/v1/auth/me').flush(me);
    await login;

    const done = firstValueFrom(session.logout());
    http.expectOne('/api/v1/auth/logout').flush({}, { status: 500, statusText: 'Server Error' });
    await done;

    expect(session.isAuthenticated()).toBe(false);
    expect(session.token()).toBeNull();
    expect(session.user()).toBeNull();
  });
});
