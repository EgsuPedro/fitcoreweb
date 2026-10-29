import { ChangeDetectionStrategy, Component, OnDestroy, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

/** Límites que el servidor vuelve a exigir: aquí solo se avisa antes de subir. */
export const LOGO_RULES = { maxBytes: 1_048_576, minSide: 32, maxSide: 2000, types: ['image/png', 'image/jpeg'] } as const;

export type LogoProblem = 'type' | 'size' | 'dimensions' | 'unreadable';

/** ¿Cumple el archivo las reglas básicas de un logo? Devuelve el primer problema o nulo. Las medidas se comprueban aparte (hay que cargar la imagen). */
export function logoFileProblem(file: { type: string; size: number }): LogoProblem | null {
  if (!(LOGO_RULES.types as readonly string[]).includes(file.type)) {
    return 'type';
  }

  return file.size > LOGO_RULES.maxBytes ? 'size' : null;
}

export function logoDimensionsProblem(width: number, height: number): LogoProblem | null {
  const bad = (n: number) => n < LOGO_RULES.minSide || n > LOGO_RULES.maxSide;
  return bad(width) || bad(height) ? 'dimensions' : null;
}

/**
 * Selector de logo con vista previa. Valida el tipo (PNG o JPG), el peso (hasta 1 MB) y las medidas antes de subirlo y emite el archivo elegido (o nulo
 * si no es válido o se quitó). No sube nada: quien lo usa decide cuándo. El servidor repite todas las validaciones y limpia los metadatos.
 */
@Component({
  selector: 'fc-logo-picker',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="logo-picker">
      <div class="logo-preview" [class.empty]="!preview() && !currentUrl()">
        @if (preview() ?? currentUrl(); as url) {
          <img [src]="url" [alt]="'branding.previewAlt' | transloco" />
        } @else {
          <span class="muted">{{ 'branding.noLogo' | transloco }}</span>
        }
      </div>
      <div class="logo-controls">
        <label class="btn" [class.disabled]="disabled()">
          {{ (preview() || currentUrl() ? 'branding.change' : 'branding.choose') | transloco }}
          <input class="sr-only" type="file" accept="image/png,image/jpeg" [disabled]="disabled()" (change)="onFile($event)" />
        </label>
        @if (preview()) {
          <button type="button" class="btn btn-ghost" [disabled]="disabled()" (click)="clear()">{{ 'branding.discard' | transloco }}</button>
        }
        <small class="muted">{{ 'branding.rules' | transloco }}</small>
        @if (problem(); as p) {
          <small class="field-error" role="alert">{{ 'branding.problem.' + p | transloco }}</small>
        }
      </div>
    </div>
  `,
})
export class LogoPicker implements OnDestroy {
  /** Logo que el gimnasio ya tiene (URL local temporal), para mostrarlo mientras no se elija otro. */
  readonly currentUrl = input<string | null>(null);
  readonly disabled = input(false);
  readonly fileChange = output<File | null>();

  protected readonly preview = signal<string | null>(null);
  protected readonly problem = signal<LogoProblem | null>(null);

  ngOnDestroy(): void {
    this.release();
  }

  /** Quita la selección (por ejemplo, tras guardar). */
  reset(): void {
    this.clear();
  }

  protected onFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    input.value = ''; // permite elegir de nuevo el mismo archivo
    if (!file) {
      return;
    }

    const early = logoFileProblem(file);
    if (early) {
      this.reject(early);
      return;
    }

    const url = URL.createObjectURL(file);
    const image = new Image();
    image.onload = () => {
      const bad = logoDimensionsProblem(image.naturalWidth, image.naturalHeight);
      if (bad) {
        URL.revokeObjectURL(url);
        this.reject(bad);
        return;
      }

      this.release();
      this.preview.set(url);
      this.problem.set(null);
      this.fileChange.emit(file);
    };
    image.onerror = () => {
      URL.revokeObjectURL(url);
      this.reject('unreadable');
    };
    image.src = url;
  }

  protected clear(): void {
    this.release();
    this.problem.set(null);
    this.fileChange.emit(null);
  }

  private reject(problem: LogoProblem): void {
    this.problem.set(problem);
    this.fileChange.emit(null);
  }

  private release(): void {
    const current = this.preview();
    if (current) {
      URL.revokeObjectURL(current);
      this.preview.set(null);
    }
  }
}
