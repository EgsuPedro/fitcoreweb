import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { TaxRate } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDate } from '../../shared/format.pipes';
import { ToastService } from '../../shared/toast';

/** Tarifas de IVA de la plataforma: historial y registro de una nueva tarifa general que rige hacia adelante. */
@Component({
  selector: 'fc-tax-rates',
  imports: [ReactiveFormsModule, TranslocoPipe, Badge, FcDate],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'taxRates.title' | transloco }}</h1>
    </header>
    <p class="muted">{{ 'taxRates.intro' | transloco }}</p>

    @if (loadError()) {
      <p class="alert alert-danger" role="alert">{{ loadError() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'taxRates.kind' | transloco }}</th>
            <th class="num">{{ 'taxRates.percent' | transloco }}</th>
            <th>{{ 'taxRates.validFrom' | transloco }}</th>
            <th>{{ 'taxRates.validUntil' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (rate of rates(); track rate.id) {
            <tr>
              <td>{{ 'enums.taxKind.' + rate.kind | transloco }}</td>
              <td class="num">{{ rate.percent }} %</td>
              <td>{{ rate.validFrom | fcDate }}</td>
              <td>{{ rate.validUntil ? (rate.validUntil | fcDate) : ('taxRates.open' | transloco) }}</td>
              <td>
                @if (rate.isCurrent) {
                  <fc-badge tone="ok">{{ 'taxRates.current' | transloco }}</fc-badge>
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    </div>

    <form class="card section" [formGroup]="form" (ngSubmit)="save()" novalidate>
      <h2>{{ 'taxRates.new' | transloco }}</h2>
      <p class="muted">{{ 'taxRates.newHint' | transloco }}</p>
      @if (formError()) {
        <p class="alert alert-danger" role="alert">{{ formError() }}</p>
      }
      <div class="form-grid">
        <label class="field">
          <span>{{ 'taxRates.percent' | transloco }}</span>
          <input type="number" min="0" max="100" step="0.01" formControlName="percent" />
        </label>
        <label class="field">
          <span>{{ 'taxRates.validFrom' | transloco }}</span>
          <input type="date" formControlName="validFrom" />
        </label>
      </div>
      <div class="inline-actions">
        <button type="submit" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
      </div>
    </form>
  `,
})
export class TaxRates implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  protected readonly rates = signal<TaxRate[]>([]);
  protected readonly saving = signal(false);
  protected readonly loadError = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    percent: [15, [Validators.required, Validators.min(0), Validators.max(100)]],
    validFrom: ['', [Validators.required]],
  });

  ngOnInit(): void {
    this.reload();
  }

  protected save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.errors.text('taxRates.invalidForm'));
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.formError.set(null);
    this.api.createTaxRate({ percent: Number(v.percent), validFrom: v.validFrom }).subscribe({
      next: () => {
        this.saving.set(false);
        this.toast.success('taxRates.saved');
        this.reload();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  private reload(): void {
    this.api.platformTaxRates().subscribe({
      next: (rates) => this.rates.set(rates),
      error: (err: unknown) => this.loadError.set(this.errors.message(err)),
    });
  }
}
