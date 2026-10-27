import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { Subject, debounceTime } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { Branch } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';

export const TIME_ZONES = ['America/Guayaquil', 'Pacific/Galapagos'] as const;

@Component({
  selector: 'fc-branches',
  imports: [ReactiveFormsModule, TranslocoPipe, Modal, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'branches.title' | transloco }}</h1>
      @if (canCreate()) {
        <button type="button" class="btn btn-primary" (click)="openCreate()">{{ 'branches.new' | transloco }}</button>
      }
    </header>

    <div class="toolbar">
      <input
        type="search"
        class="search"
        [placeholder]="'common.search' | transloco"
        [attr.aria-label]="'common.search' | transloco"
        (input)="onSearch($any($event.target).value)"
      />
      <select [attr.aria-label]="'common.status' | transloco" (change)="onFilter($any($event.target).value)">
        <option value="">{{ 'common.all' | transloco }}</option>
        <option value="true">{{ 'common.active' | transloco }}</option>
        <option value="false">{{ 'common.inactive' | transloco }}</option>
      </select>
    </div>

    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'branches.code' | transloco }}</th>
            <th>{{ 'branches.name' | transloco }}</th>
            <th>{{ 'branches.timeZone' | transloco }}</th>
            <th>{{ 'branches.sri' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (branch of items(); track branch.id) {
            <tr>
              <td><strong>{{ branch.code }}</strong></td>
              <td>{{ branch.name }}</td>
              <td>{{ branch.timeZone }}</td>
              <td>{{ branch.sriEstablishmentCode ?? '—' }}</td>
              <td>
                <span class="badge" [class.badge-ok]="branch.isActive" [class.badge-off]="!branch.isActive">
                  {{ (branch.isActive ? 'common.active' : 'common.inactive') | transloco }}
                </span>
              </td>
              <td class="actions">
                @if (canUpdate()) {
                  <button type="button" class="btn" (click)="openEdit(branch)">{{ 'common.edit' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr>
              <td colspan="6" class="empty">{{ (loading() ? 'common.loading' : 'branches.empty') | transloco }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>

    <fc-pager [page]="page()" [pageSize]="pageSize" [total]="total()" (pageChange)="goTo($event)" />

    @if (editing(); as target) {
      <fc-modal [title]="(target === 'new' ? 'branches.new' : 'branches.edit') | transloco" (closed)="closeEditor()">
        <form id="branch-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
          @if (formError()) {
            <p class="alert alert-danger" role="alert">{{ formError() }}</p>
          }
          <label class="field">
            <span>{{ 'branches.code' | transloco }}</span>
            <input formControlName="code" maxlength="20" autocapitalize="characters" />
            @if (target !== 'new') {
              <small class="muted">{{ 'branches.codeLocked' | transloco }}</small>
            }
          </label>
          <label class="field">
            <span>{{ 'branches.name' | transloco }}</span>
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
            <span>{{ 'branches.sri' | transloco }}</span>
            <input formControlName="sriEstablishmentCode" maxlength="3" inputmode="numeric" placeholder="001" />
            <small class="muted">{{ 'branches.sriHint' | transloco }}</small>
          </label>
          <label class="field">
            <span>{{ 'branches.address' | transloco }}</span>
            <input formControlName="address" maxlength="300" />
          </label>
          @if (target !== 'new') {
            <label class="check">
              <input type="checkbox" formControlName="isActive" />
              <span>{{ 'common.active' | transloco }}</span>
            </label>
          }
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="closeEditor()">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="branch-form" class="btn btn-primary" [disabled]="saving()">
            {{ 'common.save' | transloco }}
          </button>
        </ng-container>
      </fc-modal>
    }
  `,
})
export class Branches implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly timeZones = TIME_ZONES;
  protected readonly pageSize = 20;

  protected readonly canCreate = computed(() => this.session.has(Perm.BranchesCreate));
  protected readonly canUpdate = computed(() => this.session.has(Perm.BranchesUpdate));

  protected readonly items = signal<Branch[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly editing = signal<Branch | 'new' | null>(null);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);

  private search = '';
  private isActive = '';
  private readonly searchInput = new Subject<string>();

  protected readonly form = this.fb.nonNullable.group({
    code: ['', [Validators.required, Validators.maxLength(20), Validators.pattern(/^[A-Za-z0-9_-]+$/)]],
    name: ['', [Validators.required, Validators.maxLength(200)]],
    timeZone: [TIME_ZONES[0] as string, [Validators.required]],
    sriEstablishmentCode: ['', [Validators.pattern(/^[0-9]{3}$/)]],
    address: ['', [Validators.maxLength(300)]],
    isActive: [true],
  });

  constructor() {
    this.searchInput.pipe(debounceTime(300), takeUntilDestroyed(this.destroyRef)).subscribe((text) => {
      this.search = text;
      this.goTo(1);
    });
  }

  ngOnInit(): void {
    this.load();
  }

  protected onSearch(text: string): void {
    this.searchInput.next(text);
  }

  protected onFilter(value: string): void {
    this.isActive = value;
    this.goTo(1);
  }

  protected goTo(page: number): void {
    this.page.set(page);
    this.load();
  }

  protected openCreate(): void {
    this.form.reset({ code: '', name: '', timeZone: TIME_ZONES[0], sriEstablishmentCode: '', address: '', isActive: true });
    this.form.controls.code.enable();
    this.formError.set(null);
    this.editing.set('new');
  }

  protected openEdit(branch: Branch): void {
    this.form.reset({
      code: branch.code,
      name: branch.name,
      timeZone: branch.timeZone,
      sriEstablishmentCode: branch.sriEstablishmentCode ?? '',
      address: branch.address ?? '',
      isActive: branch.isActive,
    });
    this.form.controls.code.disable();
    this.formError.set(null);
    this.editing.set(branch);
  }

  protected closeEditor(): void {
    this.editing.set(null);
  }

  protected save(): void {
    const target = this.editing();
    if (!target) {
      return;
    }
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(null);
      return;
    }

    const value = this.form.getRawValue();
    const common = {
      name: value.name.trim(),
      timeZone: value.timeZone,
      sriEstablishmentCode: value.sriEstablishmentCode.trim() || null,
      address: value.address.trim() || null,
    };

    this.saving.set(true);
    this.formError.set(null);
    const request =
      target === 'new'
        ? this.api.createBranch({ code: value.code.trim(), ...common })
        : this.api.updateBranch(target.id, { ...common, isActive: value.isActive });

    request.subscribe({
      next: () => {
        this.saving.set(false);
        this.closeEditor();
        this.load();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api
      .branches({ page: this.page(), pageSize: this.pageSize, search: this.search, isActive: this.isActive })
      .subscribe({
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
