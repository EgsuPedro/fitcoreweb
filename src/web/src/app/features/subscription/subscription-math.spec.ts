import { describe, expect, it } from 'vitest';
import { alertClass, isRestricted, requestTone, sortBySeverity, usagePercent } from './subscription-math';

describe('subscription-math', () => {
  it('ordena los avisos de lo más grave a lo menos y conserva el orden entre iguales', () => {
    const sorted = sortBySeverity([
      { severity: 'Info', code: 'a' },
      { severity: 'Critical', code: 'b' },
      { severity: 'Warning', code: 'c' },
      { severity: 'Critical', code: 'd' },
    ]);
    expect(sorted.map((x) => x.code)).toEqual(['b', 'd', 'c', 'a']);
  });

  it('asigna la clase según la gravedad y una neutra por defecto', () => {
    expect(alertClass('Critical')).toBe('alert-danger');
    expect(alertClass('Warning')).toBe('alert-warning');
    expect(alertClass('Info')).toBe('alert-info');
    expect(alertClass('otra')).toBe('alert-info');
  });

  it('calcula el porcentaje de un límite sin pasar de 100 y sin inventar uno cuando no se mide', () => {
    expect(usagePercent(1, 4)).toBe(25);
    expect(usagePercent(9, 4)).toBe(100);
    expect(usagePercent(null, 4)).toBeNull();
    expect(usagePercent(3, null)).toBeNull();
    expect(usagePercent(3, 0)).toBeNull();
  });

  it('distingue los estados de una solicitud y el modo restringido', () => {
    expect(requestTone('Approved')).toBe('ok');
    expect(requestTone('Pending')).toBe('info');
    expect(requestTone('Rejected')).toBe('danger');
    expect(requestTone('Withdrawn')).toBe('off');
    expect(isRestricted('Restricted')).toBe(true);
    expect(isRestricted('Grace')).toBe(false);
  });
});
