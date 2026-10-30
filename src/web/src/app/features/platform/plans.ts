import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { CommercialPlan, CommercialPlanSummary, PlanCatalog } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcMoney } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';
import { emptyToNull, isFree, parseAmount, parseLimit } from './platform-math';

/**
 * Catálogo de planes comerciales de FIT360 (lo que la plataforma le vende a un gimnasio; no son las membresías de los alumnos). Cada cambio de
 * precio, periodicidad, prueba, límites o funciones crea una versión nueva: las suscripciones existentes conservan la suya. Un plan no se borra: se desactiva.
 */
@Component({
  selector: 'fc-platform-plans',
  imports: [ReactiveFormsModule, TranslocoPipe, Badge, FcMoney, Modal, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'platform.plans.title' | transloco }}</h1>
        <p class="muted">{{ 'platform.plans.subtitle' | transloco }}</p>
      </div>
      <button type="button" class="btn btn-primary" (click)="openCreate()">{{ 'platform.plans.new' | transloco }}</button>
    </header>

    <section class="card filters">
      <div class="filters-row">
        <label class="inline-field">
          <span>{{ 'common.search' | transloco }}</span>
          <input type="search" (input)="list.search($any($event.target).value)" />
        </label>
        <label class="inline-field">
          <span>{{ 'common.status' | transloco }}</span>
          <select (change)="list.set({ isActive: $any($event.target).value })">
            <option value="">{{ 'common.all' | transloco }}</option>
            <option value="true">{{ 'common.active' | transloco }}</option>
            <option value="false">{{ 'common.inactive' | transloco }}</option>
          </select>
        </label>
      </div>
    </section>

    @if (list.error()) { <p class="alert alert-danger" role="alert">{{ list.error() }}</p> }
    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    <div class="card table-card">
      <table class="table">
        <caption class="sr-only">{{ 'platform.plans.title' | transloco }}</caption>
        <thead>
          <tr>
            <th scope="col">{{ 'platform.plans.plan' | transloco }}</th>
            <th scope="col">{{ 'platform.plans.period' | transloco }}</th>
            <th scope="col" class="num">{{ 'platform.plans.price' | transloco }}</th>
            <th scope="col" class="num">{{ 'platform.plans.trial' | transloco }}</th>
            <th scope="col" class="num">{{ 'platform.plans.version' | transloco }}</th>
            <th scope="col">{{ 'common.status' | transloco }}</th>
            <th scope="col">{{ 'platform.plans.visibility' | transloco }}</th>
            <th scope="col"></th>
          </tr>
        </thead>
        <tbody>
          @for (p of list.items(); track p.id) {
            <tr>
              <th scope="row">{{ p.name }}<br /><small class="muted">{{ p.code }}</small></th>
              <td>{{ 'platform.period.' + p.current.billingPeriod | transloco }}</td>
              <td class="num">{{ p.current.price | fcMoney }}@if (free(p.current.price)) { <br /><small class="muted">{{ 'platform.plans.free' | transloco }}</small> }</td>
              <td class="num">{{ p.current.trialDays }}</td>
              <td class="num">v{{ p.current.number }}</td>
              <td><fc-badge [tone]="p.isActive ? 'ok' : 'off'">{{ (p.isActive ? 'common.active' : 'common.inactive') | transloco }}</fc-badge></td>
              <td><fc-badge [tone]="p.isPublic ? 'ok' : 'off'">{{ (p.isPublic ? 'platform.plans.published' : 'platform.plans.hidden') | transloco }}</fc-badge></td>
              <td class="row-actions">
                <button type="button" class="btn" (click)="openEdit(p)">{{ 'common.edit' | transloco }}</button>
                <button type="button" class="btn" (click)="toggle(p)">{{ (p.isActive ? 'platform.plans.deactivate' : 'platform.plans.activate') | transloco }}</button>
                <button type="button" class="btn" (click)="toggleVisibility(p)">{{ (p.isPublic ? 'platform.plans.hide' : 'platform.plans.publish') | transloco }}</button>
              </td>
            </tr>
          } @empty {
            <tr><td colspan="8" class="empty">{{ (list.loading() ? 'common.loading' : 'platform.plans.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @if (editing(); as plan) {
      <fc-modal [title]="(plan.id ? 'platform.plans.edit' : 'platform.plans.new') | transloco" (closed)="editing.set(null)">
        <form id="plan-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
          @if (formError()) { <p class="alert alert-danger" role="alert">{{ formError() }}</p> }
          @if (plan.id) {
            <p class="alert alert-info">{{ 'platform.plans.versionHint' | transloco }}</p>
          }
          <label class="field">
            <span>{{ 'platform.plans.code' | transloco }}</span>
            <input formControlName="code" maxlength="40" autocapitalize="none" spellcheck="false" [readonly]="!!plan.id" />
            <small class="muted">{{ 'platform.plans.codeHint' | transloco }}</small>
          </label>
          <label class="field"><span>{{ 'common.name' | transloco }}</span><input formControlName="name" maxlength="120" /></label>
          <label class="field"><span>{{ 'common.description' | transloco }}</span><textarea formControlName="description" maxlength="1000"></textarea></label>
          <label class="field"><span>{{ 'platform.plans.rules' | transloco }}</span><textarea formControlName="rules" maxlength="2000"></textarea></label>
          <div class="filters-row">
            <label class="field">
              <span>{{ 'platform.plans.period' | transloco }}</span>
              <select formControlName="billingPeriod">
                @for (b of catalog()?.billingPeriods ?? []; track b) { <option [value]="b">{{ 'platform.period.' + b | transloco }}</option> }
              </select>
            </label>
            <label class="field"><span>{{ 'platform.plans.price' | transloco }} (USD)</span><input formControlName="price" inputmode="decimal" /></label>
            <label class="field"><span>{{ 'platform.plans.trialDays' | transloco }}</span><input formControlName="trialDays" inputmode="numeric" /></label>
            <label class="field">
              <span>{{ 'platform.plans.graceDays' | transloco }}</span>
              <input formControlName="graceDays" inputmode="numeric" [placeholder]="'platform.plans.graceDefault' | transloco" />
            </label>
          </div>

          <fieldset class="field">
            <legend>{{ 'platform.plans.features' | transloco }}</legend>
            @for (f of catalog()?.features ?? []; track f.code) {
              <label class="check">
                <input type="checkbox" [checked]="features().has(f.code)" (change)="toggleFeature(f.code)" />
                <span>{{ f.name }} <small class="muted">— {{ f.description }}</small></span>
              </label>
            }
          </fieldset>

          <fieldset class="field">
            <legend>{{ 'platform.plans.limits' | transloco }}</legend>
            <small class="muted">{{ 'platform.plans.limitsHint' | transloco }}</small>
            @for (l of catalog()?.limits ?? []; track l.code) {
              <label class="inline-field">
                <span>{{ l.name }} ({{ l.unit }})</span>
                <input [value]="limits()[l.code] ?? ''" inputmode="numeric" [placeholder]="'platform.plans.unlimited' | transloco" (input)="setLimit(l.code, $any($event.target).value)" />
              </label>
            }
          </fieldset>
          <label class="field"><span>{{ 'platform.plans.changeNote' | transloco }}</span><input formControlName="changeNote" maxlength="500" /></label>
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="editing.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="plan-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }
  `,
})
export class PlatformPlans implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly fb = inject(FormBuilder);
  private readonly toast = inject(ToastService);
  private readonly transloco = inject(TranslocoService);

  protected readonly list = new ListState<CommercialPlanSummary>((q) => this.api.commercialPlans(q), this.errors, inject(DestroyRef));
  protected readonly catalog = signal<PlanCatalog | null>(null);
  protected readonly editing = signal<{ id: string | null } | null>(null);
  protected readonly features = signal<ReadonlySet<string>>(new Set());
  protected readonly limits = signal<Record<string, string>>({});
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.pattern(/^[a-z0-9][a-z0-9_-]{1,38}[a-z0-9]$/)]],
    name: ['', [Validators.required, Validators.maxLength(120)]],
    description: [''],
    rules: [''],
    billingPeriod: ['Monthly', [Validators.required]],
    price: ['0', [Validators.required]],
    trialDays: ['0', [Validators.required]],
    graceDays: [''],
    changeNote: [''],
  });

  ngOnInit(): void {
    this.list.load();
    this.api.planCatalog().subscribe({ next: (c) => this.catalog.set(c), error: (err: unknown) => this.error.set(this.errors.message(err)) });
  }

  protected free(price: number): boolean {
    return isFree(price);
  }

  protected openCreate(): void {
    this.form.reset({ code: '', name: '', description: '', rules: '', billingPeriod: 'Monthly', price: '0', trialDays: '0', graceDays: '', changeNote: '' });
    this.form.controls.code.enable();
    this.features.set(new Set());
    this.limits.set(Object.fromEntries((this.catalog()?.limits ?? []).map((l) => [l.code, ''])));
    this.formError.set(null);
    this.editing.set({ id: null });
  }

  protected openEdit(summary: CommercialPlanSummary): void {
    this.api.commercialPlan(summary.id).subscribe({
      next: (plan: CommercialPlan) => {
        const v = plan.current;
        this.form.reset({
          code: plan.code, name: plan.name, description: plan.description ?? '', rules: plan.rules ?? '', billingPeriod: v.billingPeriod,
          price: String(v.price), trialDays: String(v.trialDays), graceDays: v.graceDays === null ? '' : String(v.graceDays), changeNote: '',
        });
        this.features.set(new Set(v.features));
        this.limits.set(Object.fromEntries(v.limits.map((l) => [l.code, l.value === null ? '' : String(l.value)])));
        this.formError.set(null);
        this.editing.set({ id: plan.id });
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  protected toggleFeature(code: string): void {
    this.features.update((set) => {
      const next = new Set(set);
      if (!next.delete(code)) {
        next.add(code);
      }

      return next;
    });
  }

  protected setLimit(code: string, value: string): void {
    this.limits.update((l) => ({ ...l, [code]: value }));
  }

  protected save(): void {
    const target = this.editing();
    const v = this.form.getRawValue();
    const price = parseAmount(v.price);
    const trial = Number(v.trialDays);
    const grace = v.graceDays.trim() === '' ? null : Number(v.graceDays);
    const limits = (this.catalog()?.limits ?? []).map((l) => ({ code: l.code, value: parseLimit(this.limits()[l.code] ?? '') }));
    if (this.form.invalid || Number.isNaN(price) || !Number.isInteger(trial) || trial < 0 || (grace !== null && (!Number.isInteger(grace) || grace < 0)) || limits.some((l) => Number.isNaN(l.value))) {
      this.form.markAllAsTouched();
      this.formError.set(this.transloco.translate('platform.plans.invalid'));
      return;
    }

    const common = {
      name: v.name.trim(), description: emptyToNull(v.description), rules: emptyToNull(v.rules), currency: 'USD',
      billingPeriod: v.billingPeriod as 'Monthly' | 'Quarterly' | 'Yearly', price, trialDays: trial, graceDays: grace,
      features: [...this.features()].sort(), limits, changeNote: emptyToNull(v.changeNote),
    };
    this.saving.set(true);
    this.formError.set(null);
    const call = target?.id
      ? this.api.updateCommercialPlan(target.id, common)
      : this.api.createCommercialPlan({ ...common, code: v.code.trim().toLowerCase(), isActive: true });
    call.subscribe({
      next: () => {
        this.saving.set(false);
        this.editing.set(null);
        this.toast.success('platform.plans.saved');
        this.list.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  protected toggleVisibility(plan: CommercialPlanSummary): void {
    this.error.set(null);
    this.api.setCommercialPlanVisibility(plan.id, !plan.isPublic).subscribe({
      next: () => {
        this.toast.success(plan.isPublic ? 'platform.plans.hiddenToast' : 'platform.plans.publishedToast');
        this.list.load();
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  protected toggle(plan: CommercialPlanSummary): void {
    this.error.set(null);
    this.api.setCommercialPlanStatus(plan.id, !plan.isActive).subscribe({
      next: () => {
        this.toast.success(plan.isActive ? 'platform.plans.deactivated' : 'platform.plans.activated');
        this.list.load();
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }
}
