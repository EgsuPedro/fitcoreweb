import { DestroyRef, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoService } from '@jsverse/transloco';
import { combineLatest } from 'rxjs';
import { Seo } from './seo.service';

/**
 * Pone el título, la descripción y los datos de compartir de la página actual, y los vuelve a poner al cambiar de idioma (cuando el idioma nuevo ya cargó: por eso se usan los flujos
 * `selectTranslate` y no una traducción inmediata). Se llama una vez desde el constructor de la página. `structured` arma los datos estructurados (JSON-LD) ya traducidos.
 */
export function pageSeo(path: string, titleKey: string, descriptionKey: string, structured?: (t: TranslocoService) => unknown): void {
  const seo = inject(Seo);
  const transloco = inject(TranslocoService);
  const destroyRef = inject(DestroyRef);

  combineLatest([transloco.selectTranslate(titleKey), transloco.selectTranslate(descriptionKey)])
    .pipe(takeUntilDestroyed(destroyRef))
    .subscribe(([title, description]) => {
      seo.set({ title, description, path });
      seo.jsonLd(structured ? structured(transloco) : null);
    });
  destroyRef.onDestroy(() => seo.jsonLd(null));
}
