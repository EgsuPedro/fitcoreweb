import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { ToastService } from '../../shared/toast';

type Policy = 'RequireFullPayment' | 'AllowUntilOverdue' | 'AllowWithMinimumPayment';

/** Políticas comerciales del gimnasio: acceso con deuda, plazo de pago y descuento máximo. */
@Component({
  selector: 'fc-commercial-settings',
  imports: [ReactiveFormsModule, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'settings.title' | transloco }}</h1>
    </header>
    <p class="muted">{{ 'settings.intro' | transloco }}</p>

    @if (loadError()) {
      <p class="alert alert-danger" role="alert">{{ loadError() }}</p>
    }

    <form class="card section" [formGroup]="form" (ngSubmit)="save()" novalidate>
      @if (formError()) {
        <p class="alert alert-danger" role="alert">{{ formError() }}</p>
      }
      @if (!canManage()) {
        <p class="alert alert-info">{{ 'settings.readOnly' | transloco }}</p>
      }
      <fieldset class="field" [disabled]="!canManage()">
        <legend>{{ 'settings.accessPolicy' | transloco }}</legend>
        @for (policy of policies; track policy) {
          <label class="check">
            <input type="radio" formControlName="accessPolicy" [value]="policy" />
            <span>
              <strong>{{ 'enums.accessPolicy.' + policy | transloco }}</strong><br />
              <small class="muted">{{ 'settings.policy.' + policy | transloco }}</small>
            </span>
          </label>
        }
      </fieldset>
      <div class="form-grid">
        @if (form.controls.accessPolicy.value === 'AllowWithMinimumPayment') {
          <label class="field">
            <span>{{ 'settings.minPaymentPercent' | transloco }}</span>
            <input type="number" min="1" max="100" step="1" formControlName="minPaymentPercent" />
          </label>
        }
        <label class="field">
          <span>{{ 'settings.dueDays' | transloco }}</span>
          <input type="number" min="0" max="90" step="1" formControlName="dueDays" />
          <small class="muted">{{ 'settings.dueDaysHint' | transloco }}</small>
        </label>
        <label class="field">
          <span>{{ 'settings.maxDiscountPercent' | transloco }}</span>
          <input type="number" min="0" max="100" step="1" formControlName="maxDiscountPercent" />
        </label>
      </div>
      @if (canManage()) {
        <div class="inline-actions">
          <button type="submit" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
        </div>
      }
    </form>
  `,
})
export class CommercialSettingsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  protected readonly policies: Policy[] = ['RequireFullPayment', 'AllowUntilOverdue', 'AllowWithMinimumPayment'];
  protected readonly canManage = computed(() => this.session.has(Perm.CommercialSettingsManage));
  protected readonly saving = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    accessPolicy: ['AllowUntilOverdue' as Policy],
    minPaymentPercent: [50, [Validators.min(0), Validators.max(100)]],
    dueDays: [7, [Validators.required, Validators.min(0), Validators.max(90)]],
    maxDiscountPercent: [0, [Validators.required, Validators.min(0), Validators.max(100)]],
  });

  ngOnInit(): void {
    this.api.commercialSettings().subscribe({
      next: (settings) => this.form.reset({ ...settings, accessPolicy: settings.accessPolicy as Policy }),
      error: (err: unknown) => this.loadError.set(this.errors.message(err)),
    });
  }

  protected save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.errors.text('settings.invalidForm'));
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .updateCommercialSettings({
        accessPolicy: v.accessPolicy,
        minPaymentPercent: Number(v.minPaymentPercent),
        dueDays: Number(v.dueDays),
        maxDiscountPercent: Number(v.maxDiscountPercent),
      })
      .subscribe({
        next: (settings) => {
          this.saving.set(false);
          this.form.reset({ ...settings, accessPolicy: settings.accessPolicy as Policy });
          this.toast.success('settings.saved');
        },
        error: (err: unknown) => {
          this.saving.set(false);
          this.formError.set(this.errors.message(err));
        },
      });
  }
}
