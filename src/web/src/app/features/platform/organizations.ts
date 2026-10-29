import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { CommercialPlanSummary, OrganizationSummary } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDate } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { subscriptionTone } from './platform-math';

const STATUSES = ['Trialing', 'Active', 'PastDue', 'Suspended', 'Expired', 'None'] as const;

/**
 * Organizaciones clientes (gimnasios). El equipo de plataforma ve el contrato, el estado, el próximo vencimiento y el consumo agregado de cada una:
 * nunca datos de sus alumnos. El consumo es una foto con su fecha de medición.
 */
@Component({
  selector: 'fc-platform-organizations',
  imports: [RouterLink, TranslocoPipe, Badge, Pager, FcDate],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'platform.organizations.title' | transloco }}</h1>
        <p class="muted">{{ 'platform.organizations.subtitle' | transloco }}</p>
      </div>
    </header>

    <section class="card filters" [attr.aria-label]="'platform.organizations.filters' | transloco">
      <div class="filters-row">
        <label class="inline-field">
          <span>{{ 'common.search' | transloco }}</span>
          <input type="search" (input)="list.search($any($event.target).value)" [placeholder]="'platform.organizations.searchHint' | transloco" />
        </label>
        <label class="inline-field">
          <span>{{ 'platform.organizations.tenantStatus' | transloco }}</span>
          <select (change)="list.set({ tenantStatus: $any($event.target).value })">
            <option value="">{{ 'common.all' | transloco }}</option>
            <option value="Active">{{ 'platform.tenantStatus.Active' | transloco }}</option>
            <option value="Suspended">{{ 'platform.tenantStatus.Suspended' | transloco }}</option>
          </select>
        </label>
        <label class="inline-field">
          <span>{{ 'platform.organizations.subscription' | transloco }}</span>
          <select (change)="list.set({ subscriptionStatus: $any($event.target).value })">
            <option value="">{{ 'common.all' | transloco }}</option>
            @for (s of statuses; track s) {
              <option [value]="s">{{ 'platform.status.' + s | transloco }}</option>
            }
          </select>
        </label>
        <label class="inline-field">
          <span>{{ 'platform.organizations.plan' | transloco }}</span>
          <select (change)="list.set({ planCode: $any($event.target).value })">
            <option value="">{{ 'common.all' | transloco }}</option>
            @for (p of plans(); track p.id) {
              <option [value]="p.code">{{ p.name }}</option>
            }
          </select>
        </label>
        <label class="inline-field">
          <span>{{ 'platform.organizations.dueWithin' | transloco }}</span>
          <select (change)="list.set({ dueWithinDays: $any($event.target).value })">
            <option value="">{{ 'common.all' | transloco }}</option>
            <option value="7">{{ 'platform.organizations.days' | transloco: { n: 7 } }}</option>
            <option value="15">{{ 'platform.organizations.days' | transloco: { n: 15 } }}</option>
            <option value="30">{{ 'platform.organizations.days' | transloco: { n: 30 } }}</option>
          </select>
        </label>
      </div>
    </section>

    @if (list.error()) {
      <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <caption class="sr-only">{{ 'platform.organizations.title' | transloco }}</caption>
        <thead>
          <tr>
            <th scope="col">{{ 'platform.organizations.organization' | transloco }}</th>
            <th scope="col">{{ 'platform.organizations.plan' | transloco }}</th>
            <th scope="col">{{ 'common.status' | transloco }}</th>
            <th scope="col">{{ 'platform.organizations.nextDue' | transloco }}</th>
            <th scope="col" class="num">{{ 'platform.usage.branches' | transloco }}</th>
            <th scope="col" class="num">{{ 'platform.usage.users' | transloco }}</th>
            <th scope="col" class="num">{{ 'platform.usage.members' | transloco }}</th>
            <th scope="col"></th>
          </tr>
        </thead>
        <tbody>
          @for (o of list.items(); track o.id) {
            <tr>
              <th scope="row">
                <a [routerLink]="['/platform/organizations', o.id]">{{ o.name }}</a><br />
                <small class="muted">{{ o.slug }} · {{ o.createdAt | fcDate }}</small>
              </th>
              <td>{{ o.subscription?.planName ?? '—' }}</td>
              <td>
                @if (o.subscription; as s) {
                  <fc-badge [tone]="tone(s.status)">{{ 'platform.status.' + s.status | transloco }}</fc-badge>
                } @else {
                  <fc-badge tone="off">{{ 'platform.status.None' | transloco }}</fc-badge>
                }
                @if (o.tenantStatus !== 'Active') {
                  <fc-badge tone="danger">{{ 'platform.tenantStatus.' + o.tenantStatus | transloco }}</fc-badge>
                }
              </td>
              <td>{{ o.subscription?.nextDueOn | fcDate }}</td>
              <td class="num">{{ o.usage?.branches ?? '—' }}</td>
              <td class="num">{{ o.usage?.internalUsers ?? '—' }}</td>
              <td class="num">{{ o.usage?.members ?? '—' }}</td>
              <td class="row-actions"><a class="btn" [routerLink]="['/platform/organizations', o.id]">{{ 'platform.organizations.open' | transloco }}</a></td>
            </tr>
          } @empty {
            <tr><td colspan="8" class="empty">{{ (list.loading() ? 'common.loading' : 'platform.organizations.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
    <p class="muted">{{ 'platform.organizations.usageNote' | transloco }}</p>
  `,
})
export class PlatformOrganizations implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);

  protected readonly statuses = STATUSES;
  protected readonly plans = signal<CommercialPlanSummary[]>([]);
  protected readonly list = new ListState<OrganizationSummary>((q) => this.api.organizations(q), this.errors, inject(DestroyRef));

  ngOnInit(): void {
    this.list.load();
    this.api.commercialPlans({ pageSize: 100 }).subscribe({ next: (r) => this.plans.set(r.items), error: () => this.plans.set([]) });
  }

  protected tone(status: string) {
    return subscriptionTone(status);
  }
}
