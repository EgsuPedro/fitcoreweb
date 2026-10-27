import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { MemberForm } from './member-form';
import type { Member } from '../../core/api/models';

@Component({
  selector: 'fc-members',
  imports: [RouterLink, TranslocoPipe, Badge, Pager, MemberForm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'members.title' | transloco }}</h1>
      @if (canCreate()) {
        <button type="button" class="btn btn-primary" (click)="editing.set('new')">{{ 'members.new' | transloco }}</button>
      }
    </header>

    <div class="toolbar">
      <input
        type="search"
        class="search"
        [placeholder]="'members.searchHint' | transloco"
        [attr.aria-label]="'members.searchHint' | transloco"
        (input)="list.search($any($event.target).value)"
      />
      <select [attr.aria-label]="'common.status' | transloco" (change)="list.set({ status: $any($event.target).value })">
        <option value="">{{ 'common.all' | transloco }}</option>
        <option value="Active">{{ 'enums.memberStatus.Active' | transloco }}</option>
        <option value="Inactive">{{ 'enums.memberStatus.Inactive' | transloco }}</option>
      </select>
      <select [attr.aria-label]="'common.branch' | transloco" (change)="list.set({ branchId: $any($event.target).value })">
        <option value="">{{ 'common.allBranches' | transloco }}</option>
        @for (branch of branches(); track branch.id) {
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
            <th>{{ 'members.code' | transloco }}</th>
            <th>{{ 'members.name' | transloco }}</th>
            <th>{{ 'members.document' | transloco }}</th>
            <th>{{ 'members.phone' | transloco }}</th>
            <th>{{ 'common.branch' | transloco }}</th>
            <th>{{ 'common.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (member of list.items(); track member.id) {
            <tr>
              <td><strong>{{ member.code }}</strong></td>
              <td>
                <a [routerLink]="['/members', member.id]">{{ member.firstName }} {{ member.lastName }}</a>
              </td>
              <td>{{ 'enums.idType.' + member.idType | transloco }} {{ member.idNumber }}</td>
              <td>{{ member.phone ?? '—' }}</td>
              <td>{{ catalog.nameOf(member.branchId) }}</td>
              <td>
                <fc-badge [tone]="member.status === 'Active' ? 'ok' : 'off'">{{ 'enums.memberStatus.' + member.status | transloco }}</fc-badge>
              </td>
              <td class="actions">
                <a class="btn" [routerLink]="['/members', member.id]">{{ 'common.view' | transloco }}</a>
                @if (canUpdate()) {
                  <button type="button" class="btn" (click)="editing.set(member)">{{ 'common.edit' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr>
              <td colspan="7" class="empty">{{ (list.loading() ? 'common.loading' : 'members.empty') | transloco }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>

    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @if (editing(); as target) {
      <fc-member-form [member]="target === 'new' ? null : target" (closed)="editing.set(null)" (saved)="onSaved()" />
    }
  `,
})
export class Members implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly list = new ListState<Member>((query) => this.api.members(query), inject(ErrorMessages), inject(DestroyRef));
  protected readonly branches = this.catalog.branches;
  protected readonly editing = signal<Member | 'new' | null>(null);
  protected readonly canCreate = computed(() => this.session.has(Perm.MembersCreate));
  protected readonly canUpdate = computed(() => this.session.has(Perm.MembersUpdate));

  ngOnInit(): void {
    this.catalog.ensure();
    this.list.load();
  }

  protected onSaved(): void {
    this.editing.set(null);
    this.list.load();
  }
}
