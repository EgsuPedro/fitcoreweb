import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { Subject, debounceTime } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { AuditLog } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Pager } from '../../shared/pager';

const ENTITIES = ['Branch', 'AppUser', 'AppRole', 'RolePermission', 'AppUserRole', 'UserBranch', 'Tenant', 'RefreshToken'] as const;

@Component({
  selector: 'fc-audit',
  imports: [TranslocoPipe, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'audit.title' | transloco }}</h1>
    </header>

    <div class="toolbar">
      <select [attr.aria-label]="'audit.entity' | transloco" (change)="onEntity($any($event.target).value)">
        <option value="">{{ 'common.all' | transloco }}</option>
        @for (entity of entities; track entity) {
          <option [value]="entity">{{ entity }}</option>
        }
      </select>
      <input
        type="search"
        class="search"
        [placeholder]="'audit.entityId' | transloco"
        [attr.aria-label]="'audit.entityId' | transloco"
        (input)="entityIdInput.next($any($event.target).value)"
      />
    </div>

    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'audit.when' | transloco }}</th>
            <th>{{ 'audit.action' | transloco }}</th>
            <th>{{ 'audit.entity' | transloco }}</th>
            <th>{{ 'audit.changes' | transloco }}</th>
          </tr>
        </thead>
        <tbody>
          @for (log of items(); track log.id) {
            <tr>
              <td>{{ format.dateTime(log.occurredAt) }}</td>
              <td><code>{{ log.action }}</code></td>
              <td class="mono">{{ log.entityId ?? '—' }}</td>
              <td>
                @if (log.before || log.after) {
                  <details>
                    <summary>{{ 'audit.show' | transloco }}</summary>
                    @if (log.before) {
                      <p class="muted">{{ 'audit.before' | transloco }}</p>
                      <pre>{{ pretty(log.before) }}</pre>
                    }
                    @if (log.after) {
                      <p class="muted">{{ 'audit.after' | transloco }}</p>
                      <pre>{{ pretty(log.after) }}</pre>
                    }
                  </details>
                } @else {
                  <span class="muted">—</span>
                }
              </td>
            </tr>
          } @empty {
            <tr>
              <td colspan="4" class="empty">{{ (loading() ? 'common.loading' : 'audit.empty') | transloco }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>

    <fc-pager [page]="page()" [pageSize]="pageSize" [total]="total()" (pageChange)="goTo($event)" />
  `,
})
export class Audit implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly format = inject(Formatter);

  protected readonly entities = ENTITIES;
  protected readonly pageSize = 25;
  protected readonly entityIdInput = new Subject<string>();

  protected readonly items = signal<AuditLog[]>([]);
  protected readonly total = signal(0);
  protected readonly page = signal(1);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  private entity = '';
  private entityId = '';

  constructor() {
    this.entityIdInput.pipe(debounceTime(300), takeUntilDestroyed(this.destroyRef)).subscribe((text) => {
      this.entityId = text.trim();
      this.goTo(1);
    });
  }

  ngOnInit(): void {
    this.load();
  }

  protected onEntity(value: string): void {
    this.entity = value;
    this.goTo(1);
  }

  protected goTo(page: number): void {
    this.page.set(page);
    this.load();
  }

  protected pretty(json: string): string {
    try {
      return JSON.stringify(JSON.parse(json), null, 2);
    } catch {
      return json;
    }
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.auditLogs({ page: this.page(), pageSize: this.pageSize, entity: this.entity, entityId: this.entityId }).subscribe({
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
