import { HttpErrorResponse } from '@angular/common/http';
import { AttemptKey } from './idempotency';

describe('AttemptKey', () => {
  it('conserva la clave entre lecturas hasta que se renueva', () => {
    const key = new AttemptKey();
    const first = key.value;
    expect(key.value).toBe(first);
    key.renew();
    expect(key.value).not.toBe(first);
  });

  it('tras un corte de red (estado 0) conserva la clave: la solicitud pudo llegar', () => {
    const key = new AttemptKey();
    const first = key.value;
    key.afterError(new HttpErrorResponse({ status: 0 }));
    expect(key.value).toBe(first);
  });

  it('tras cualquier respuesta del servidor renueva la clave', () => {
    for (const status of [400, 403, 409, 500]) {
      const key = new AttemptKey();
      const first = key.value;
      key.afterError(new HttpErrorResponse({ status }));
      expect(key.value).not.toBe(first);
    }
  });
});
