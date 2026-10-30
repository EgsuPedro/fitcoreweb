import { isPlatformBrowser } from '@angular/common';
import { PLATFORM_ID, inject } from '@angular/core';
import { CanActivateFn, CanMatchFn, Router } from '@angular/router';
import { SessionService } from './session.service';

/** Exige sesión. Al recargar la página recupera la sesión con la cookie de refresh antes de decidir. */
export const authGuard: CanActivateFn = async (_route, state) => {
  const session = inject(SessionService);
  const router = inject(Router);

  await session.restore();
  if (!session.isAuthenticated()) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }

  // Con una clave temporal pendiente de cambio solo se puede ir a cambiarla (el servidor tampoco da permisos hasta entonces).
  const mustChange = session.user()?.mustChangePassword ?? false;
  return mustChange && !state.url.startsWith('/change-password') ? router.createUrlTree(['/change-password']) : true;
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

/** ¿Hay sesión? La recupera con la cookie de refresh. En el prerenderizado (servidor) nunca la hay, así que se genera la versión pública. */
async function isSignedIn(): Promise<boolean> {
  if (!isPlatformBrowser(inject(PLATFORM_ID))) {
    return false;
  }
  const session = inject(SessionService);
  await session.restore();
  return session.isAuthenticated();
}

/** «/» muestra el panel a quien inició sesión y el sitio comercial a los visitantes: esta ruta es la del panel. */
export const signedInMatch: CanMatchFn = () => isSignedIn();

/** Contraparte de signedInMatch: la portada pública solo se ofrece a quien no tiene sesión. */
export const visitorMatch: CanMatchFn = async () => !(await isSignedIn());

/** Las páginas públicas internas (planes, contacto…) solo existen con una ruta: la dirección vacía («/») es del panel o de la portada, nunca de un marco vacío. */
export const hasPathMatch: CanMatchFn = (_route, segments) => segments.length > 0;
