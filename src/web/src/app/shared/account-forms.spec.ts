import { FormControl, FormGroup } from '@angular/forms';
import { describe, expect, it } from 'vitest';
import { SLUG_PATTERN, matchFields, passwordPolicy, suggestSlug } from './account-forms';

describe('passwordPolicy', () => {
  const check = (value: string) => passwordPolicy(new FormControl(value));

  it('acepta 10 o más caracteres con mayúscula, minúscula y número', () => {
    expect(check('ClaveSegura2026')).toBeNull();
    expect(check('Abcdefghi1')).toBeNull();
  });

  it('rechaza lo corto o al que le falta una clase de carácter', () => {
    expect(check('Corta1A')).not.toBeNull();
    expect(check('sinmayusculas123')).not.toBeNull();
    expect(check('SINMINUSCULAS123')).not.toBeNull();
    expect(check('SinNumerosAquiii')).not.toBeNull();
  });

  it('un valor vacío no lo juzga (eso es «obligatorio»)', () => {
    expect(check('')).toBeNull();
  });
});

describe('matchFields', () => {
  const group = (a: string, b: string) => {
    const g = new FormGroup({ password: new FormControl(a), confirm: new FormControl(b) }, { validators: matchFields('password', 'confirm') });
    g.updateValueAndValidity();
    return g;
  };

  it('marca el segundo campo cuando no coinciden y lo limpia cuando coinciden', () => {
    const g = group('Uno12345678', 'Otro12345678');
    expect(g.controls.confirm.errors).toEqual({ mismatch: true });

    g.controls.confirm.setValue('Uno12345678');
    expect(g.controls.confirm.errors).toBeNull();
  });
});

describe('suggestSlug', () => {
  it('quita acentos y símbolos y separa con guiones', () => {
    expect(suggestSlug('Gimnasio Atlético Quito')).toBe('gimnasio-atletico-quito');
    expect(suggestSlug('  ¡Fit & Fuerza!  ')).toBe('fit-fuerza');
  });

  it('respeta el máximo de 40 caracteres sin dejar un guion al final', () => {
    const slug = suggestSlug('Centro de Alto Rendimiento Deportivo y Acondicionamiento Físico');
    expect(slug.length).toBeLessThanOrEqual(40);
    expect(slug.endsWith('-')).toBe(false);
    expect(SLUG_PATTERN.test(slug)).toBe(true);
  });

  it('devuelve vacío si no alcanza para un identificador válido', () => {
    expect(suggestSlug('')).toBe('');
    expect(suggestSlug('Ab')).toBe('');
    expect(suggestSlug('!!!')).toBe('');
  });

  it('todo lo que sugiere cumple el patrón del servidor', () => {
    for (const name of ['Mi Gym', 'Gym 24/7', 'Ñandú Fitness', 'CrossFit Quito Norte']) {
      expect(SLUG_PATTERN.test(suggestSlug(name)), name).toBe(true);
    }
  });
});
