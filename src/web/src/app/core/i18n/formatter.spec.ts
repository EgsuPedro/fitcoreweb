import { TestBed } from '@angular/core/testing';
import { Translation, TranslocoLoader, provideTransloco, TranslocoService } from '@jsverse/transloco';
import { of } from 'rxjs';
import { Formatter } from './messages';

class Loader implements TranslocoLoader {
  getTranslation() {
    return of({} as Translation);
  }
}

describe('Formatter', () => {
  let format: Formatter;
  let transloco: TranslocoService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideTransloco({ config: { availableLangs: ['es', 'en'], defaultLang: 'es' }, loader: Loader })],
    });
    format = TestBed.inject(Formatter);
    transloco = TestBed.inject(TranslocoService);
  });

  it('una fecha sin hora no se corre un día por la zona horaria', () => {
    transloco.setActiveLang('en');
    expect(format.dateOnly('2026-11-26')).toContain('26');
    expect(format.dateOnly('2026-01-01')).toContain('1');
    expect(format.dateOnly('2026-01-01')).toContain('2026');
  });

  it('formatea dólares en el idioma activo', () => {
    transloco.setActiveLang('en');
    expect(format.money(1234.5)).toBe('$1,234.50');
  });
});
