import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { SessionService } from './session.service';

/** Exige sesión. Al recargar la página recupera la sesión con la cookie de refresh antes de decidir. */
export const authGuard: CanActivateFn = async (_route, state) => {
  const session = inject(SessionService);
  const router = inject(Router);

  await session.restore();
  return session.isAuthenticated() || router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

/** Para el login: quien ya tiene sesión va directo al inicio. */
export const guestGuard: CanActivateFn = async () => {
  const session = inject(SessionService);
  const router = inject(Router);

  await session.restore();
  return session.isAuthenticated() ? router.createUrlTree(['/']) : true;
};

/**
 * Exige el permiso indicado en `data.permission` de la ruta. Es solo experiencia de usuario: la seguridad real la
 * aplica la API en cada solicitud.
 */
export const permissionGuard: CanActivateFn = (route) => {
  const session = inject(SessionService);
  const router = inject(Router);

  const required = route.data['permission'] as string | undefined;
  return !required || session.has(required) || router.createUrlTree(['/forbidden']);
};
