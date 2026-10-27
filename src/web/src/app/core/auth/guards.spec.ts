import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { authGuard, guestGuard, permissionGuard } from './guards';
import { SessionService } from './session.service';

function fakeSession(authenticated: boolean, permissions: string[] = []) {
  return {
    restore: vi.fn().mockResolvedValue(undefined),
    isAuthenticated: () => authenticated,
    has: (permission: string) => permissions.includes(permission),
  };
}

function setup(session: ReturnType<typeof fakeSession>) {
  const createUrlTree = vi.fn((commands: unknown[], extras?: unknown) => ({ commands, extras }) as unknown as UrlTree);
  TestBed.configureTestingModule({
    providers: [
      { provide: SessionService, useValue: session },
      { provide: Router, useValue: { createUrlTree } },
    ],
  });
  return { createUrlTree };
}

const state = (url: string) => ({ url }) as RouterStateSnapshot;
const route = (data: Record<string, unknown> = {}) => ({ data }) as unknown as ActivatedRouteSnapshot;

describe('guards', () => {
  it('authGuard deja pasar a quien tiene sesión, tras recuperarla', async () => {
    const session = fakeSession(true);
    setup(session);

    const result = await TestBed.runInInjectionContext(() => authGuard(route(), state('/branches')));

    expect(result).toBe(true);
    expect(session.restore).toHaveBeenCalled();
  });

  it('authGuard manda al login conservando la ruta pedida', async () => {
    const { createUrlTree } = setup(fakeSession(false));

    await TestBed.runInInjectionContext(() => authGuard(route(), state('/users?page=2')));

    expect(createUrlTree).toHaveBeenCalledWith(['/login'], { queryParams: { returnUrl: '/users?page=2' } });
  });

  it('guestGuard manda al inicio a quien ya inició sesión', async () => {
    const { createUrlTree } = setup(fakeSession(true));

    await TestBed.runInInjectionContext(() => guestGuard(route(), state('/login')));

    expect(createUrlTree).toHaveBeenCalledWith(['/']);
  });

  it('guestGuard permite ver el login sin sesión', async () => {
    setup(fakeSession(false));

    expect(await TestBed.runInInjectionContext(() => guestGuard(route(), state('/login')))).toBe(true);
  });

  it('permissionGuard exige el permiso de la ruta', () => {
    const { createUrlTree } = setup(fakeSession(true, ['branches.read']));

    expect(TestBed.runInInjectionContext(() => permissionGuard(route({ permission: 'branches.read' }), state('/branches')))).toBe(true);
    TestBed.runInInjectionContext(() => permissionGuard(route({ permission: 'roles.read' }), state('/roles')));

    expect(createUrlTree).toHaveBeenCalledWith(['/forbidden']);
  });

  it('permissionGuard no restringe rutas sin permiso declarado', () => {
    setup(fakeSession(true));

    expect(TestBed.runInInjectionContext(() => permissionGuard(route(), state('/')))).toBe(true);
  });
});
