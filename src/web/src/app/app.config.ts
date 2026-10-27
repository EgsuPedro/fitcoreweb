import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { routes } from './app.routes';
import { authInterceptor } from './core/auth/auth.interceptor';
import { initialLang, provideI18n } from './core/i18n/transloco';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideI18n(),
    // Carga el idioma antes de mostrar nada y mantiene <html lang> sincronizado con el idioma activo.
    provideAppInitializer(() => {
      const transloco = inject(TranslocoService);
      const lang = initialLang();
      transloco.langChanges$.subscribe((active) => (document.documentElement.lang = active));
      transloco.setActiveLang(lang);
      return firstValueFrom(transloco.load(lang));
    }),
  ],
};
