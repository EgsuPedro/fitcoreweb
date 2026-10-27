import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Tenant } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Confirm } from '../../shared/confirm';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { TIME_ZONES } from '../branches/branches';

const LOCALES = ['es-EC', 'es', 'en'] as const;

@Component({
  selector: 'fc-tenants',
  imports: [ReactiveFormsModule, TranslocoPipe, Modal, Pager, Confirm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'tenants.title' | transloco }}</h1>
      <button type="button" class="btn btn-primary" (click)="openCreate()">{{ 'tenants.new' | transloco }}</button>
    </header>

    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'tenants.slug' | transloco }}</th>
            <th>{{ 'tenants.name' | transloco }}</th>
            <th>{{ 'branches.timeZone' | transloco }}</th>
            <th>{{ 'tenants.created' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (tenant of items(); track tenant.id) {
            <tr>
              <td><strong>{{ tenant.slug }}</strong></td>
              <td>{{ tenant.name }}</td>
              <td>{{ tenant.timeZone }}</td>
              <td>{{ format.date(tenant.createdAt) }}</td>
              <td>
                <span class="badge" [class.badge-ok]="tenant.status === 'Active'" [class.badge-off]="tenant.status !== 'Active'">
                  {{ (tenant.status === 'Active' ? 'common.active' : 'tenants.suspended') | transloco }}
                </span>
              </td>
              <td class="actions">
                @if (tenant.slug !== 'platform') {
                  <button type="button" class="btn" [class.btn-danger]="tenant.status === 'Active'" (click)="statusTarget.set(tenant)">
                    {{ (tenant.status === 'Active' ? 'tenants.suspend' : 'tenants.activate') | transloco }}
                  </button>
                }
              </td>
            </tr>
          } @empty {
            <tr>
              <td colspan="6" class="empty">{{ (loading() ? 'common.loading' : 'tenants.empty') | transloco }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>

    <fc-pager [page]="page()" [pageSize]="pageSize" [total]="total()" (pageChange)="goTo($event)" />

    @if (creating()) {
      <fc-modal [title]="'tenants.new' | transloco" (closed)="creating.set(false)">
        <form id="tenant-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
          @if (formError()) {
            <p class="alert alert-danger" role="alert">{{ formError() }}</p>
          }
          <label class="field">
            <span>{{ 'tenants.slug' | transloco }}</span>
            <input formControlName="slug" maxlength="40" autocapitalize="none" spellcheck="false" />
            <small class="muted">{{ 'tenants.slugHint' | transloco }}</small>
          </label>
          <label class="field">
            <span>{{ 'tenants.name' | transloco }}</span>
            <input formControlName="name" maxlength="200" />
          </label>
          <label class="field">
            <span>{{ 'branches.timeZone' | transloco }}</span>
            <select formControlName="timeZone">
              @for (zone of timeZones; track zone) {
                <option [value]="zone">{{ zone }}</option>
              }
            </select>
          </label>
          <label class="field">
            <span>{{ 'tenants.locale' | transloco }}</span>
            <select formControlName="locale">
              @for (locale of locales; track locale) {
                <option [value]="locale">{{ locale }}</option>
              }
            </select>
          </label>
          <h3>{{ 'tenants.owner' | transloco }}</h3>
          <label class="field">
            <span>{{ 'users.name' | transloco }}</span>
            <input formControlName="ownerFullName" maxlength="200" autocomplete="off" />
          </label>
          <label class="field">
            <span>{{ 'users.email' | transloco }}</span>
            <input formControlName="ownerEmail" type="email" maxlength="256" autocomplete="off" />
          </label>
          <label class="field">
            <span>{{ 'users.password' | transloco }}</span>
            <input formControlName="ownerPassword" type="password" autocomplete="new-password" />
            <small class="muted">{{ 'users.passwordHint' | transloco }}</small>
          </label>
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="creating.set(false)">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="tenant-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (statusTarget(); as target) {
      <fc-confirm
        [title]="(target.status === 'Active' ? 'tenants.suspend' : 'tenants.activate') | transloco"
        [message]="(target.status === 'Active' ? 'tenants.suspendConfirm' : 'tenants.activateConfirm') | transloco: { name: target.name }"
        [confirmLabel]="(target.status === 'Active' ? 'tenants.suspend' : 'tenants.activate') | transloco"
        [danger]="target.status === 'Active'"
        (confirmed)="changeStatus(target)"
        (cancelled)="statusTarget.set(null)"
      />
    }
  `,
})
export class Tenants implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly transloco = inject(TranslocoService);
  private readonly fb = inject(FormBuilder);
  protected readonly format = inject(Formatter);

  protected readonly timeZones = TIME_ZONES;
  protected readonly locales = LOCALES;
  protected readonly pageSize = 20;

  protected readonly items = signal<Tenant[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly creating = signal(false);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);
  protected readonly statusTarget = signal<Tenant | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    slug: ['', [Validators.required, Validators.pattern(/^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$/)]],
    name: ['', [Validators.required, Validators.maxLength(200)]],
    timeZone: [TIME_ZONES[0] as string, [Validators.required]],
    locale: ['es-EC', [Validators.required]],
    ownerFullName: ['', [Validators.required, Validators.maxLength(200)]],
    ownerEmail: ['', [Validators.required, Validators.email]],
    ownerPassword: ['', [Validators.required]],
  });

  ngOnInit(): void {
    this.load();
  }

  protected goTo(page: number): void {
    this.page.set(page);
    this.load();
  }

  protected openCreate(): void {
    this.form.reset({
      slug: '',
      name: '',
      timeZone: TIME_ZONES[0],
      locale: 'es-EC',
      ownerFullName: '',
      ownerEmail: '',
      ownerPassword: '',
    });
    this.formError.set(null);
    this.creating.set(true);
  }

  protected save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(this.transloco.translate('tenants.invalidForm'));
      return;
    }

    const value = this.form.getRawValue();
    this.saving.set(true);
    this.formError.set(null);
    this.api
      .createTenant({
        ...value,
        slug: value.slug.trim().toLowerCase(),
        name: value.name.trim(),
        ownerEmail: value.ownerEmail.trim(),
        ownerFullName: value.ownerFullName.trim(),
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.creating.set(false);
          this.load();
        },
        error: (err: unknown) => {
          this.saving.set(false);
          this.formError.set(this.errors.message(err));
        },
      });
  }

  protected changeStatus(tenant: Tenant): void {
    this.statusTarget.set(null);
    this.api.setTenantStatus(tenant.id, tenant.status !== 'Active').subscribe({
      next: () => this.load(),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.tenants({ page: this.page(), pageSize: this.pageSize }).subscribe({
      next: (result) => {
        this.items.set(result.items);
        this.total.set(result.totalCount);
        this.loading.set(false);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
