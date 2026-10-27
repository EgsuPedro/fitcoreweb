import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Membership } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, membershipTone } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { FcDate, FcMoney } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { EnrollDialog } from './enroll-dialog';

type Tab = 'all' | 'expiring';

@Component({
  selector: 'fc-memberships',
  imports: [RouterLink, TranslocoPipe, Badge, Pager, FcDate, FcMoney, EnrollDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'memberships.title' | transloco }}</h1>
      @if (canCreate()) {
        <button type="button" class="btn btn-primary" (click)="enrolling.set(true)">{{ 'memberships.new' | transloco }}</button>
      }
    </header>

    <div class="tabs" role="tablist">
      <button type="button" role="tab" class="tab" [attr.aria-selected]="tab() === 'all'" [class.active]="tab() === 'all'" (click)="setTab('all')">
        {{ 'memberships.tabAll' | transloco }}
      </button>
      <button type="button" role="tab" class="tab" [attr.aria-selected]="tab() === 'expiring'" [class.active]="tab() === 'expiring'" (click)="setTab('expiring')">
        {{ 'memberships.tabExpiring' | transloco }}
      </button>
    </div>

    <div class="toolbar">
      @if (tab() === 'all') {
        <input type="search" class="search" [placeholder]="'common.search' | transloco" [attr.aria-label]="'common.search' | transloco" (input)="list.search($any($event.target).value)" />
        <select [attr.aria-label]="'common.status' | transloco" (change)="list.set({ status: $any($event.target).value })">
          <option value="">{{ 'common.all' | transloco }}</option>
          @for (status of statuses; track status) {
            <option [value]="status">{{ 'enums.membershipStatus.' + status | transloco }}</option>
          }
        </select>
      } @else {
        <label class="inline-field">
          <span>{{ 'memberships.daysAhead' | transloco }}</span>
          <select (change)="list.set({ days: $any($event.target).value })">
            @for (d of dayOptions; track d) {
              <option [value]="d" [selected]="d === 7">{{ 'memberships.days' | transloco: { count: d } }}</option>
            }
          </select>
        </label>
      }
      <select [attr.aria-label]="'common.branch' | transloco" (change)="list.set({ branchId: $any($event.target).value })">
        <option value="">{{ 'common.allBranches' | transloco }}</option>
        @for (branch of catalog.branches(); track branch.id) {
          <option [value]="branch.id">{{ branch.name }}</option>
        }
      </select>
    </div>

    @if (list.error()) {
      <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'memberships.member' | transloco }}</th>
            <th>{{ 'memberships.plan' | transloco }}</th>
            <th>{{ 'memberships.start' | transloco }}</th>
            <th>{{ 'memberships.end' | transloco }}</th>
            <th class="num">{{ 'memberships.price' | transloco }}</th>
            <th class="num">{{ 'memberships.balance' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (m of list.items(); track m.id) {
            <tr>
              <td><a [routerLink]="['/members', m.memberId]">{{ m.memberName }}</a> <small class="muted">{{ m.memberCode }}</small></td>
              <td>{{ m.planName }}</td>
              <td>{{ m.startDate | fcDate }}</td>
              <td>{{ m.endDate | fcDate }}</td>
              <td class="num">{{ m.agreedPrice | fcMoney }}</td>
              <td class="num">{{ m.obligation?.balance | fcMoney }}</td>
              <td><fc-badge [tone]="tone(m.status)">{{ 'enums.membershipStatus.' + m.status | transloco }}</fc-badge></td>
              <td class="actions"><a class="btn" [routerLink]="['/memberships', m.id]">{{ 'common.view' | transloco }}</a></td>
            </tr>
          } @empty {
            <tr><td colspan="8" class="empty">{{ (list.loading() ? 'common.loading' : 'memberships.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @if (enrolling()) {
      <fc-enroll-dialog (closed)="enrolling.set(false)" (done)="onEnrolled($event)" />
    }
  `,
})
export class Memberships implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly router = inject(Router);
  private readonly errors = inject(ErrorMessages);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly tab = signal<Tab>('all');
  protected readonly list = new ListState<Membership>(
    (query) => (this.tab() === 'expiring' ? this.api.expiringMemberships(query) : this.api.memberships(query)),
    this.errors,
    inject(DestroyRef),
  );
  protected readonly statuses = ['PendingPayment', 'Active', 'Frozen', 'Expired', 'Cancelled'];
  protected readonly dayOptions = [3, 7, 15, 30];
  protected readonly tone = membershipTone;
  protected readonly enrolling = signal(false);
  protected readonly canCreate = computed(() => this.session.has(Perm.MembershipsCreate));

  ngOnInit(): void {
    this.catalog.ensure();
    this.list.load();
  }

  protected setTab(tab: Tab): void {
    if (tab === this.tab()) {
      return;
    }
    this.tab.set(tab);
    // Los filtros de una pestaña no tienen sentido en la otra.
    this.list.set({ status: undefined, search: undefined, days: tab === 'expiring' ? 7 : undefined });
  }

  protected onEnrolled(membership: Membership): void {
    this.enrolling.set(false);
    void this.router.navigate(['/memberships', membership.id]);
  }
}
