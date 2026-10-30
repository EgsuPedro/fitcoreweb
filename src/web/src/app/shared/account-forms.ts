import { AbstractControl, ValidationErrors, ValidatorFn } from '@angular/forms';

/** Misma política que el servidor (Identity): al menos 10 caracteres con mayúscula, minúscula y número. El servidor vuelve a validar; esto solo evita un viaje inútil. */
export const passwordPolicy: ValidatorFn = (control: AbstractControl): ValidationErrors | null => {
  const value = String(control.value ?? '');
  if (value.length === 0) {
    return null; // «obligatorio» lo valida otro validador
  }
  return value.length >= 10 && /[a-z]/.test(value) && /[A-Z]/.test(value) && /\d/.test(value) ? null : { passwordPolicy: true };
};

/** Que dos campos del grupo coincidan (por ejemplo, contraseña y su confirmación). El error queda en el segundo campo. */
export function matchFields(first: string, second: string): ValidatorFn {
  return (group: AbstractControl): ValidationErrors | null => {
    const a = group.get(first);
    const b = group.get(second);
    if (!a || !b) {
      return null;
    }
    const mismatch = a.value !== b.value;
    const errors = { ...(b.errors ?? {}) };
    if (mismatch) {
      errors['mismatch'] = true;
    } else {
      delete errors['mismatch'];
    }
    b.setErrors(Object.keys(errors).length > 0 ? errors : null);
    return null;
  };
}

/**
 * Identificador sugerido para un gimnasio a partir de su nombre: minúsculas, sin acentos, letras y números separados por guiones, de 3 a 40 caracteres. Devuelve vacío si el
 * nombre no da para uno válido. Es solo una sugerencia: la persona puede cambiarla y el servidor decide si está libre.
 */
export function suggestSlug(name: string): string {
  const base = name
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 40)
    .replace(/-+$/g, '');
  return base.length >= 3 ? base : '';
}

/** Mismo patrón que el servidor: 3 a 40 caracteres, minúsculas, números y guiones, sin empezar ni terminar en guion. */
export const SLUG_PATTERN = /^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/;
