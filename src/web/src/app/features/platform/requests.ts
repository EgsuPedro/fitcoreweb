import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { PlatformRequest } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDateTime } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';
import { requestTone } from '../subscription/subscription-math';
import { subscriptionTone } from './platform-math';

const STATUSES = ['Pending', 'Approved', 'Rejected', 'Withdrawn'] as const;

interface Resolving {
  request: PlatformRequest;
  decision: 'approve' | 'reject';
}

/**
 * Solicitudes que los gimnasios hacen sobre su suscripción (cambio de plan, cancelación, reactivación). Aprobar ejecuta la acción con las reglas de
 * siempre y queda auditado; rechazar exige una razón que el gimnasio leerá en su portal.
 */
@Component({
  selector: 'fc-platform-requests',
  imports: [RouterLink, TranslocoPipe, Badge, FcDateTime, Modal, Pager],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'platform.requests.title' | transloco }}</h1>
        <p class="muted">{{ 'platform.requests.subtitle' | transloco }}</p>
      </div>
    </header>

    <section class="card filters" [attr.aria-label]="'platform.organizations.filters' | transloco">
      <div class="filters-row">
        <label class="inline-field">
          <span>{{ 'common.status' | transloco }}</span>
          <select (change)="list.set({ status: $any($event.target).value })">
            <option value="">{{ 'common.all' | transloco }}</option>
            @for (s of statuses; track s) {
              <option [value]="s" [selected]="s === 'Pending'">{{ 'mySubscription.requestStatus.' + s | transloco }}</option>
            }
          </select>
        </label>
      </div>
    </section>

    @if (list.error()) { <p class="alert alert-danger" role="alert">{{ list.error() }}</p> }

    <div class="card table-card">
      <table class="table">
        <caption class="sr-only">{{ 'platform.requests.title' | transloco }}</caption>
        <thead>
          <tr>
            <th scope="col">{{ 'platform.organizations.organization' | transloco }}</th>
            <th scope="col">{{ 'mySubscription.request' | transloco }}</th>
            <th scope="col">{{ 'common.status' | transloco }}</th>
            <th scope="col"></th>
          </tr>
        </thead>
        <tbody>
          @for (r of list.items(); track r.id) {
            <tr>
              <th scope="row">
                <a [routerLink]="['/platform/organizations', r.tenantId]">{{ r.tenantName }}</a><br />
                <small class="muted">{{ r.currentPlanName }} · <fc-badge [tone]="subTone(r.subscriptionStatus ?? '')">{{ 'platform.status.' + r.subscriptionStatus | transloco }}</fc-badge></small>
              </th>
              <td>
                <strong>{{ 'mySubscription.kind.' + r.kind | transloco }}</strong>@if (r.requestedPlanName) { → {{ r.requestedPlanName }} }<br />
                <small class="muted">{{ r.createdAt | fcDateTime }}</small>
                @if (r.message) { <br /><small>{{ r.message }}</small> }
                @if (r.resolutionNote) { <br /><small class="muted">{{ 'mySubscription.answer' | transloco }}: {{ r.resolutionNote }}</small> }
              </td>
              <td><fc-badge [tone]="tone(r.status)">{{ 'mySubscription.requestStatus.' + r.status | transloco }}</fc-badge></td>
              <td class="row-actions">
                @if (r.status === 'Pending' && canManage()) {
                  <button type="button" class="btn btn-primary" (click)="open(r, 'approve')">{{ 'platform.requests.approve' | transloco }}</button>
                  <button type="button" class="btn btn-danger" (click)="open(r, 'reject')">{{ 'platform.requests.reject' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="4" class="empty">{{ (list.loading() ? 'common.loading' : 'platform.requests.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @if (resolving(); as c) {
      <fc-modal [title]="('platform.requests.' + c.decision) | transloco" (closed)="resolving.set(null)">
        @if (resolveError()) { <p class="alert alert-danger" role="alert">{{ resolveError() }}</p> }
        <p><strong>{{ c.request.tenantName }}</strong> · {{ 'mySubscription.kind.' + c.request.kind | transloco }}@if (c.request.requestedPlanName) { → {{ c.request.requestedPlanName }} }</p>
        <p class="muted">{{ 'platform.requests.' + c.decision + 'Hint' | transloco }}</p>
        <label class="field">
          <span>{{ (c.decision === 'reject' ? 'platform.requests.reasonRequired' : 'platform.requests.noteOptional') | transloco }}</span>
          <textarea [value]="note()" maxlength="500" (input)="note.set($any($event.target).value)"></textarea>
        </label>
        <ng-container footer>
          <button type="button" class="btn" (click)="resolving.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn" [class.btn-danger]="c.decision === 'reject'" [class.btn-primary]="c.decision === 'approve'" [disabled]="busy()" (click)="resolve(c)">{{ 'platform.requests.confirm' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }
  `,
})
export class PlatformRequests implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly session = inject(SessionService);
  private readonly transloco = inject(TranslocoService);

  protected readonly statuses = STATUSES;
  protected readonly list = new ListState<PlatformRequest>((q) => this.api.platformRequests(q), this.errors, inject(DestroyRef), 20, { status: 'Pending' });
  protected readonly resolving = signal<Resolving | null>(null);
  protected readonly note = signal('');
  protected readonly resolveError = signal<string | null>(null);
  protected readonly busy = signal(false);

  ngOnInit(): void {
    this.list.load();
  }

  protected canManage(): boolean {
    return this.session.has(Perm.PlatformSubscriptionsManage);
  }

  protected tone(status: string) {
    return requestTone(status);
  }

  protected subTone(status: string) {
    return subscriptionTone(status);
  }

  protected open(request: PlatformRequest, decision: 'approve' | 'reject'): void {
    this.note.set('');
    this.resolveError.set(null);
    this.resolving.set({ request, decision });
  }

  protected resolve(c: Resolving): void {
    const note = this.note().trim();
    if (c.decision === 'reject' && note.length === 0) {
      this.resolveError.set(this.transloco.translate('errors.subscription_requests.note_required'));
      return;
    }

    this.busy.set(true);
    this.api.resolveSubscriptionRequest(c.request.id, c.decision, note || null).subscribe({
      next: () => {
        this.busy.set(false);
        this.resolving.set(null);
        this.toast.success(c.decision === 'approve' ? 'platform.requests.approved' : 'platform.requests.rejected');
        this.list.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.resolveError.set(this.errors.message(err));
      },
    });
  }
}
