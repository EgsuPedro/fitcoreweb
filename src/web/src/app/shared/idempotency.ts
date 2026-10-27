import { HttpErrorResponse } from '@angular/common/http';

/**
 * Clave de idempotencia de una operación que mueve dinero. Se reutiliza en los reintentos (un corte de red, un doble clic)
 * para que el servidor no duplique el efecto, y se renueva cuando la operación termina o el servidor la rechaza.
 */
export class AttemptKey {
  private current: string = crypto.randomUUID();

  get value(): string {
    return this.current;
  }

  renew(): void {
    this.current = crypto.randomUUID();
  }

  /** Tras un error: sin conexión (estado 0) se conserva la clave, porque la solicitud pudo llegar; cualquier otra respuesta la renueva. */
  afterError(error: unknown): void {
    if (!(error instanceof HttpErrorResponse && error.status === 0)) {
      this.renew();
    }
  }
}