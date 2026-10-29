import { describe, expect, it } from 'vitest';
import { LOGO_RULES, logoDimensionsProblem, logoFileProblem } from './logo-picker';

describe('logo-picker', () => {
  it('acepta PNG y JPG dentro del peso y rechaza el resto de tipos (SVG puede llevar scripts)', () => {
    expect(logoFileProblem({ type: 'image/png', size: 1000 })).toBeNull();
    expect(logoFileProblem({ type: 'image/jpeg', size: 1000 })).toBeNull();
    expect(logoFileProblem({ type: 'image/svg+xml', size: 1000 })).toBe('type');
    expect(logoFileProblem({ type: 'image/webp', size: 1000 })).toBe('type');
    expect(logoFileProblem({ type: '', size: 1000 })).toBe('type');
  });

  it('rechaza el archivo de más de 1 MB pero admite justo 1 MB', () => {
    expect(logoFileProblem({ type: 'image/png', size: LOGO_RULES.maxBytes })).toBeNull();
    expect(logoFileProblem({ type: 'image/png', size: LOGO_RULES.maxBytes + 1 })).toBe('size');
  });

  it('exige entre 32 y 2000 píxeles por lado', () => {
    expect(logoDimensionsProblem(32, 32)).toBeNull();
    expect(logoDimensionsProblem(2000, 2000)).toBeNull();
    expect(logoDimensionsProblem(31, 100)).toBe('dimensions');
    expect(logoDimensionsProblem(100, 2001)).toBe('dimensions');
  });
});
