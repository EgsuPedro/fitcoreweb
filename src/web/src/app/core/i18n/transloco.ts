import { HttpClient } from '@angular/common/http';
import { Injectable, inject, isDevMode } from '@angular/core';
import { Translation, TranslocoLoader, TranslocoService, provideTransloco } from '@jsverse/transloco';
import { Observable } from 'rxjs';

export const SUPPORTED_LANGS = ['es', 'en'] as const;
export type Lang = (typeof SUPPORTED_LANGS)[number];
export const DEFAULT_LANG: Lang = 'es';
const STORAGE_KEY = 'fc_lang';

@Injectable({ providedIn: 'root' })
export class TranslocoHttpLoader implements TranslocoLoader {
  private readonly http = inject(HttpClient);

  getTranslation(lang: string): Observable<Translation> {
    return this.http.get<Translation>(`/i18n/${lang}.json`);
  }
}

export function provideI18n() {
  return provideTransloco({
    config: {
      availableLangs: [...SUPPORTED_LANGS],
      defaultLang: DEFAULT_LANG,
      fallbackLang: DEFAULT_LANG,
      reRenderOnLangChange: true,
      prodMode: !isDevMode(),
    },
    loader: TranslocoHttpLoader,
  });
}

function isLang(value: string | null | undefined): value is Lang {
  return SUPPORTED_LANGS.includes(value as Lang);
}

/** Idioma guardado por el usuario; si no hay, español (idioma por defecto del producto). */
export function initialLang(): Lang {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (isLang(stored)) {
      return stored;
    }
  } catch {
    // Sin acceso a localStorage (modo privado, etc.): se usa el idioma por defecto.
  }
  return DEFAULT_LANG;
}

export function setLang(transloco: TranslocoService, lang: Lang): void {
  transloco.setActiveLang(lang);
  try {
    localStorage.setItem(STORAGE_KEY, lang);
  } catch {
    // Preferencia no persistida: se mantiene solo en esta sesión.
  }
}
