import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { ClassAttendance, Member } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { ListState } from '../../shared/list-state';
import { MemberPicker } from '../../shared/member-picker';
import { Pager } from '../../shared/pager';
import { DEFAULT_TIME_ZONE, addDays, toInstant } from '../../shared/zoned-time';

/** Historial de participación en clases: quién asistió y quién no, con filtros por alumno, resultado, sucursal y fechas. */
@Component({
  selector: 'fc-class-attendance',
  imports: [TranslocoPipe, Badge, Pager, MemberPicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'classAttendance.title' | transloco }}</h1>
    </header>

    <div class="toolbar">
      <select [attr.aria-label]="'common.branch' | transloco" (change)="list.set({ branchId: $any($event.target).value })">
        <option value="">{{ 'common.allBranches' | transloco }}</option>
        @for (branch of catalog.branches(); track branch.id) {
          <option [value]="branch.id">{{ branch.name }}</option>
        }
      </select>
      <select [attr.aria-label]="'classAttendance.outcome' | transloco" (change)="list.set({ outcome: $any($event.target).value })">
        <option value="">{{ 'common.all' | transloco }}</option>
        <option value="Attended">{{ 'enums.attendanceOutcome.Attended' | transloco }}</option>
        <option value="NoShow">{{ 'enums.attendanceOutcome.NoShow' | transloco }}</option>
      </select>
      <label class="inline-field">
        <span>{{ 'common.from' | transloco }}</span>
        <input type="date" (change)="setFrom($any($event.target).value)" />
      </label>
      <label class="inline-field">
        <span>{{ 'common.to' | transloco }}</span>
        <input type="date" (change)="setTo($any($event.target).value)" />
      </label>
    </div>
    <div class="toolbar">
      <div class="picker-inline">
        <fc-member-picker (picked)="onMember($event)" />
      </div>
    </div>

    @if (list.error()) {
      <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'schedule.when' | transloco }}</th>
            <th>{{ 'schedule.class' | transloco }}</th>
            <th>{{ 'schedule.member' | transloco }}</th>
            <th>{{ 'classAttendance.outcome' | transloco }}</th>
            <th>{{ 'common.notes' | transloco }}</th>
            <th>{{ 'classAttendance.recordedBy' | transloco }}</th>
          </tr>
        </thead>
        <tbody>
          @for (a of list.items(); track a.id) {
            <tr>
              <td>{{ when(a) }}</td>
              <td>{{ a.className }}<br /><small class="muted">{{ a.branchName }} · {{ a.instructorName }}</small></td>
              <td>{{ a.memberName }}<br /><small class="muted">{{ a.memberCode }}</small></td>
              <td>
                <fc-badge [tone]="a.outcome === 'Attended' ? 'ok' : 'danger'">{{ 'enums.attendanceOutcome.' + a.outcome | transloco }}</fc-badge>
                @if (a.hasGymEntry) {
                  <small class="muted"> · {{ 'schedule.gymEntry' | transloco }}</small>
                }
                @if (a.correctedAt) {
                  <small class="muted"> · {{ 'classAttendance.corrected' | transloco }}</small>
                }
              </td>
              <td>{{ a.notes ?? '—' }}</td>
              <td>{{ a.recordedByName }}</td>
            </tr>
          } @empty {
            <tr><td colspan="6" class="empty">{{ (list.loading() ? 'common.loading' : 'classAttendance.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
  `,
})
export class ClassAttendancePage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly format = inject(Formatter);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly list = new ListState<ClassAttendance>((query) => this.api.classAttendance(query), this.errors, inject(DestroyRef));

  ngOnInit(): void {
    this.catalog.ensure();
    this.list.load();
  }

  protected when(a: ClassAttendance): string {
    const zone = this.catalog.branches().find((b) => b.id === a.branchId)?.timeZone ?? DEFAULT_TIME_ZONE;
    return this.format.dateTimeIn(a.startsAt, zone);
  }

  protected onMember(member: Member | null): void {
    this.list.set({ memberId: member?.id ?? '' });
  }

  protected setFrom(date: string): void {
    this.list.set({ from: date ? toInstant(date, '00:00') : '' });
  }

  /** Hasta el final del día elegido (el servidor excluye el instante final). */
  protected setTo(date: string): void {
    this.list.set({ to: date ? toInstant(addDays(date, 1), "00:00") : '' });
  }
}
