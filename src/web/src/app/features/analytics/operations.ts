import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { AttendanceReport, ClassCancellationsReport, ClassOccupancyReport } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { LineChart } from '../../shared/line-chart';
import { Pager } from '../../shared/pager';
import { ExportButton } from './export-button';
import { KpiCard } from './kpi-card';
import { ReportData } from './report-data';
import { ReportFilterBar, ReportFilters } from './report-filters';
import { TabBar, queryTab } from './tabs';

const PAGE_SIZE = 20;
const ALL_TABS = [
  { id: 'attendance', labelKey: 'analytics.operations.tabs.attendance' },
  { id: 'occupancy', labelKey: 'analytics.operations.tabs.occupancy' },
  { id: 'cancellations', labelKey: 'analytics.operations.tabs.cancellations' },
] as const;
const CLASS_TABS = ALL_TABS.filter((t) => t.id !== 'attendance');
const ACTIVITIES = ['Yoga', 'Spinning', 'Functional', 'Zumba', 'Pilates', 'Stretching', 'Boxing', 'Dance', 'Other'] as const;

/**
 * Asistencia al gimnasio y reportes de clases. Quien tiene el permiso de operación ve todo; el entrenador (ruta «Mis clases») ve solo la ocupación y
 * las cancelaciones de sus propias sesiones: el servidor filtra y lo avisa con `instructorOnly`.
 */
@Component({
  selector: 'fc-analytics-operations',
  imports: [TranslocoPipe, Badge, LineChart, Pager, KpiCard, TabBar, ReportFilterBar, ExportButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ (instructorMode ? 'analytics.operations.myClasses' : 'analytics.operations.title') | transloco }}</h1>
        <p class="muted">{{ (instructorMode ? 'analytics.operations.myClassesSubtitle' : 'analytics.operations.subtitle') | transloco }}</p>
      </div>
    </header>

    <fc-report-filters />
    <fc-tab-bar [tabs]="tabs" [active]="tab.current()" (select)="tab.go($event)" />

    @switch (tab.current()) {
      @case ('attendance') {
        <div class="toolbar">
          <label class="inline-field">
            <span>{{ 'analytics.operations.grouping' | transloco }}</span>
            <select [value]="grouping()" (change)="grouping.set($any($event.target).value)">
              @for (g of groupings; track g) { <option [value]="g">{{ 'analytics.operations.groupings.' + g | transloco }}</option> }
            </select>
          </label>
          <label class="check"><input type="checkbox" [checked]="byMember()" (change)="byMember.set($any($event.target).checked); page.set(1)" /> <span>{{ 'analytics.operations.byMember' | transloco }}</span></label>
          <fc-export-button report="Attendance" />
        </div>
        @if (attendance.error()) { <p class="alert alert-danger" role="alert">{{ attendance.error() }}</p> }
        @if (attendance.data(); as a) {
          <div class="kpi-grid">
            @for (k of a.summary; track k.key) { <fc-kpi [kpi]="k" /> }
          </div>
          <h3>{{ 'analytics.operations.visitsOverTime' | transloco }}</h3>
          <fc-line-chart [points]="visitPoints(a)" [label]="'analytics.operations.visitsOverTime' | transloco" [decimals]="0" />
          <div class="grid-2">
            <section class="section">
              <h3>{{ 'analytics.operations.byWeekday' | transloco }}</h3>
              <ul class="bars" [attr.aria-label]="'analytics.operations.byWeekday' | transloco">
                @for (d of a.byWeekday; track d.key) {
                  <li><span class="bar-label">{{ 'analytics.weekdays.' + d.key | transloco }}</span><span class="bar-track" aria-hidden="true"><span class="bar-fill" [style.width.%]="share(d.visits, maxWeekday(a))"></span></span><span class="bar-value">{{ d.visits }}</span></li>
                }
              </ul>
            </section>
            <section class="section">
              <h3>{{ 'analytics.operations.byHour' | transloco }}</h3>
              <ul class="bars" [attr.aria-label]="'analytics.operations.byHour' | transloco">
                @for (h of busyHours(a); track h.key) {
                  <li><span class="bar-label">{{ h.key }}:00</span><span class="bar-track" aria-hidden="true"><span class="bar-fill" [style.width.%]="share(h.visits, maxHour(a))"></span></span><span class="bar-value">{{ h.visits }}</span></li>
                } @empty { <li class="muted">{{ 'analytics.empty' | transloco }}</li> }
              </ul>
            </section>
          </div>
          <div class="grid-2">
            <section class="section">
              <h3>{{ 'analytics.operations.byBranch' | transloco }}</h3>
              <div class="card table-card"><table class="table"><thead><tr><th>{{ 'common.branch' | transloco }}</th><th class="num">{{ 'analytics.kpi.visits.name' | transloco }}</th><th class="num">{{ 'analytics.kpi.unique_visitors.name' | transloco }}</th><th class="num">{{ 'analytics.financial.share' | transloco }}</th></tr></thead><tbody>
                @for (b of a.byBranch; track b.branchId) { <tr><td>{{ b.branchName }}</td><td class="num">{{ b.visits }}</td><td class="num">{{ b.visitors }}</td><td class="num">{{ b.share === null ? '—' : b.share + ' %' }}</td></tr> }
              </tbody></table></div>
            </section>
            <section class="section">
              <h3>{{ 'analytics.operations.frequency' | transloco }}</h3>
              <div class="card table-card"><table class="table"><thead><tr><th>{{ 'analytics.operations.visitsRange' | transloco }}</th><th class="num">{{ 'analytics.operations.membersCount' | transloco }}</th></tr></thead><tbody>
                @for (f of a.frequency; track f.label) { <tr><td>{{ f.label }}</td><td class="num">{{ f.members }}</td></tr> }
              </tbody></table></div>
            </section>
          </div>
          @if (a.members; as members) {
            <h3>{{ 'analytics.operations.byMember' | transloco }}</h3>
            <div class="card table-card">
              <table class="table">
                <thead><tr><th>{{ 'analytics.financial.member' | transloco }}</th><th>{{ 'common.branch' | transloco }}</th><th class="num">{{ 'analytics.kpi.visits.name' | transloco }}</th><th class="num">{{ 'analytics.operations.days' | transloco }}</th><th>{{ 'analytics.operations.lastVisit' | transloco }}</th></tr></thead>
                <tbody>
                  @for (m of members.items; track m.memberCode) {
                    <tr><td>{{ m.memberName }}<br /><small class="muted">{{ m.memberCode }}</small></td><td>{{ m.branchName }}</td><td class="num">{{ m.visits }}</td><td class="num">{{ m.days }}</td><td>{{ when(m.lastVisit, a.header.timeZone) }}</td></tr>
                  } @empty { <tr><td colspan="5" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
                </tbody>
              </table>
            </div>
            <fc-pager [page]="page()" [pageSize]="pageSize" [total]="members.totalCount" (pageChange)="page.set($event)" />
          }
        }
      }

      @case ('occupancy') {
        <div class="toolbar">
          <label class="inline-field">
            <span>{{ 'analytics.operations.activity' | transloco }}</span>
            <select [value]="activity()" (change)="activity.set($any($event.target).value); page.set(1)">
              <option value="">{{ 'common.all' | transloco }}</option>
              @for (a of activities; track a) { <option [value]="a">{{ 'enums.activityType.' + a | transloco }}</option> }
            </select>
          </label>
          @if (!instructorMode) { <fc-export-button report="ClassOccupancy" [extra]="{ activity: activity() || null }" /> }
        </div>
        @if (occupancy.error()) { <p class="alert alert-danger" role="alert">{{ occupancy.error() }}</p> }
        @if (occupancy.data(); as o) {
          @if (o.instructorOnly) { <p class="alert alert-info">{{ 'analytics.operations.instructorOnly' | transloco }}</p> }
          <div class="kpi-grid">
            @for (k of o.summary; track k.key) { <fc-kpi [kpi]="k" /> }
          </div>
          <dl class="summary session-breakdown">
            <dt>{{ 'analytics.sessions.held' | transloco }}</dt><dd>{{ o.sessions.held }}</dd>
            <dt>{{ 'analytics.sessions.cancelled' | transloco }}</dt><dd>{{ o.sessions.cancelled }}</dd>
            <dt>{{ 'analytics.sessions.upcoming' | transloco }}</dt><dd>{{ o.sessions.upcoming }}</dd>
            <dt>{{ 'analytics.sessions.pendingClosure' | transloco }}</dt><dd>{{ o.sessions.pendingClosure }}</dd>
          </dl>
          <p class="muted">{{ 'analytics.operations.occupancyNote' | transloco }}</p>
          <div class="grid-2">
            <section class="section">
              <h3>{{ 'analytics.operations.demand' | transloco }}</h3>
              <div class="card table-card"><table class="table"><thead><tr><th>{{ 'analytics.operations.activity' | transloco }}</th><th class="num">{{ 'analytics.operations.confirmed' | transloco }}</th><th class="num">{{ 'analytics.operations.waiting' | transloco }}</th><th class="num">{{ 'analytics.kpi.occupancy_reserved.name' | transloco }}</th></tr></thead><tbody>
                @for (a of o.byActivity; track a.key) { <tr><td>{{ 'enums.activityType.' + a.name | transloco }}</td><td class="num">{{ a.confirmed }}</td><td class="num">{{ a.waitlisted }}</td><td class="num">{{ a.occupancyPercent === null ? '—' : a.occupancyPercent + ' %' }}</td></tr> }
                @empty { <tr><td colspan="4" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody></table></div>
            </section>
            <section class="section">
              <h3>{{ 'analytics.operations.topSlots' | transloco }}</h3>
              <div class="card table-card"><table class="table"><thead><tr><th>{{ 'analytics.operations.slot' | transloco }}</th><th class="num">{{ 'analytics.operations.sessions' | transloco }}</th><th class="num">{{ 'analytics.kpi.occupancy_reserved.name' | transloco }}</th></tr></thead><tbody>
                @for (s of o.topSlots; track s.weekday + s.hour) { <tr><td>{{ 'analytics.weekdays.' + s.weekday | transloco }} {{ s.hour }}:00</td><td class="num">{{ s.sessions }}</td><td class="num">{{ s.occupancyPercent === null ? '—' : s.occupancyPercent + ' %' }}</td></tr> }
                @empty { <tr><td colspan="3" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody></table></div>
            </section>
          </div>
          <h3>{{ 'analytics.operations.byClass' | transloco }}</h3>
          <div class="card table-card"><table class="table"><thead><tr><th>{{ 'schedule.class' | transloco }}</th><th class="num">{{ 'analytics.operations.sessions' | transloco }}</th><th class="num">{{ 'classes.capacity' | transloco }}</th><th class="num">{{ 'analytics.operations.confirmed' | transloco }}</th><th class="num">{{ 'analytics.operations.waiting' | transloco }}</th><th class="num">{{ 'analytics.kpi.occupancy_reserved.name' | transloco }}</th></tr></thead><tbody>
            @for (c of o.byClass; track c.key) { <tr><td>{{ c.name }}</td><td class="num">{{ c.sessions }}</td><td class="num">{{ c.capacity }}</td><td class="num">{{ c.confirmed }}</td><td class="num">{{ c.waitlisted }}</td><td class="num">{{ c.occupancyPercent === null ? '—' : c.occupancyPercent + ' %' }}</td></tr> }
            @empty { <tr><td colspan="6" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
          </tbody></table></div>
          @if (!o.instructorOnly) {
            <h3>{{ 'analytics.operations.performance' | transloco }}</h3>
            <div class="card table-card"><table class="table"><thead><tr><th>{{ 'common.branch' | transloco }}</th><th class="num">{{ 'analytics.sessions.held' | transloco }}</th><th class="num">{{ 'analytics.sessions.cancelled' | transloco }}</th><th class="num">{{ 'analytics.kpi.occupancy_reserved.name' | transloco }}</th><th class="num">{{ 'analytics.kpi.no_shows.name' | transloco }}</th></tr></thead><tbody>
              @for (b of o.byBranch; track b.branchId) { <tr><td>{{ b.branchName }}</td><td class="num">{{ b.held }}</td><td class="num">{{ b.cancelled }}</td><td class="num">{{ b.occupancyPercent === null ? ('analytics.noData' | transloco) : b.occupancyPercent + ' %' }}</td><td class="num">{{ b.noShows }}</td></tr> }
            </tbody></table></div>
          }
          <h3>{{ 'analytics.operations.sessionList' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'schedule.when' | transloco }}</th><th>{{ 'schedule.class' | transloco }}</th><th>{{ 'common.status' | transloco }}</th><th class="num">{{ 'analytics.operations.confirmed' | transloco }} / {{ 'classes.capacity' | transloco }}</th><th class="num">{{ 'analytics.operations.waiting' | transloco }}</th><th class="num">{{ 'analytics.kpi.occupancy_reserved.name' | transloco }}</th></tr></thead>
              <tbody>
                @for (s of o.sessionList.items; track s.sessionId) {
                  <tr>
                    <td>{{ when(s.startsAt, o.header.timeZone) }}</td>
                    <td>{{ s.className }}<br /><small class="muted">{{ s.instructorName }} · {{ s.branchName }}</small></td>
                    <td><fc-badge [tone]="sessionTone(s.status)">{{ 'analytics.sessions.status.' + s.status | transloco }}</fc-badge></td>
                    <td class="num">{{ s.confirmed }} / {{ s.capacity }}</td>
                    <td class="num">{{ s.waitlisted }}</td>
                    <td class="num">{{ s.occupancyPercent === null ? '—' : s.occupancyPercent + ' %' }}</td>
                  </tr>
                } @empty { <tr><td colspan="6" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="page()" [pageSize]="pageSize" [total]="o.sessionList.totalCount" (pageChange)="page.set($event)" />
        }
      }

      @case ('cancellations') {
        <div class="toolbar">@if (!instructorMode) { <fc-export-button report="ClassCancellations" /> }</div>
        @if (cancellations.error()) { <p class="alert alert-danger" role="alert">{{ cancellations.error() }}</p> }
        @if (cancellations.data(); as c) {
          @if (c.instructorOnly) { <p class="alert alert-info">{{ 'analytics.operations.instructorOnly' | transloco }}</p> }
          <div class="kpi-grid">
            @for (k of c.summary; track k.key) { <fc-kpi [kpi]="k" /> }
          </div>
          <h3>{{ 'analytics.operations.cancelByClass' | transloco }}</h3>
          <div class="card table-card"><table class="table"><thead><tr><th>{{ 'schedule.class' | transloco }}</th><th class="num">{{ 'analytics.operations.sessions' | transloco }}</th><th class="num">{{ 'analytics.sessions.cancelled' | transloco }}</th><th class="num">%</th></tr></thead><tbody>
            @for (b of c.byClass; track b.className) { <tr><td>{{ b.className }}</td><td class="num">{{ b.sessions }}</td><td class="num">{{ b.cancelled }}</td><td class="num">{{ b.cancelRatePercent === null ? '—' : b.cancelRatePercent + ' %' }}</td></tr> }
            @empty { <tr><td colspan="4" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
          </tbody></table></div>
          <h3>{{ 'analytics.operations.cancelledSessions' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead><tr><th>{{ 'schedule.when' | transloco }}</th><th>{{ 'schedule.class' | transloco }}</th><th>{{ 'analytics.financial.reason' | transloco }}</th><th class="num">{{ 'analytics.operations.affected' | transloco }}</th></tr></thead>
              <tbody>
                @for (s of c.cancelledSessions.items; track s.sessionId) {
                  <tr><td>{{ when(s.startsAt, c.header.timeZone) }}</td><td>{{ s.className }}<br /><small class="muted">{{ s.instructorName }} · {{ s.branchName }}</small></td><td>{{ s.reason ?? '—' }}</td><td class="num">{{ s.reservationsAffected }}</td></tr>
                } @empty { <tr><td colspan="4" class="empty">{{ 'analytics.empty' | transloco }}</td></tr> }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="page()" [pageSize]="pageSize" [total]="c.cancelledSessions.totalCount" (pageChange)="page.set($event)" />
        }
      }
    }
  `,
})
export class AnalyticsOperations {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly format = inject(Formatter);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly filters = inject(ReportFilters);

  /** La ruta «Mis clases» es la misma pantalla sin asistencia al gimnasio (el entrenador no tiene ese permiso). */
  protected readonly instructorMode = inject(ActivatedRoute).snapshot.data['instructor'] === true;
  protected readonly tabs = this.instructorMode ? CLASS_TABS : ALL_TABS;
  protected readonly tab = queryTab(this.tabs.map((t) => t.id), this.instructorMode ? 'occupancy' : 'attendance');
  protected readonly pageSize = PAGE_SIZE;
  protected readonly groupings = ['Auto', 'Day', 'Week', 'Month'] as const;
  protected readonly activities = ACTIVITIES;

  protected readonly page = signal(1);
  protected readonly grouping = signal<string>('Auto');
  protected readonly byMember = signal(false);
  protected readonly activity = signal('');

  protected readonly attendance = new ReportData<AttendanceReport>((q) => this.api.attendanceReport(q), this.errors, this.destroyRef);
  protected readonly occupancy = new ReportData<ClassOccupancyReport>((q) => this.api.classOccupancyReport(q), this.errors, this.destroyRef);
  protected readonly cancellations = new ReportData<ClassCancellationsReport>((q) => this.api.classCancellationsReport(q), this.errors, this.destroyRef);
  protected readonly maxWeekday = (a: AttendanceReport) => Math.max(1, ...a.byWeekday.map((d) => d.visits));
  protected readonly maxHour = (a: AttendanceReport) => Math.max(1, ...a.byHour.map((h) => h.visits));
  protected readonly hasData = computed(() => this.attendance.data() !== null);

  constructor() {
    effect(() => {
      const query = this.filters.query();
      const tab = this.tab.current();
      const page = this.page();
      const grouping = this.grouping();
      const byMember = this.byMember();
      const activity = this.activity();
      if (!query) {
        return;
      }
      untracked(() => {
        const paged = { page, pageSize: PAGE_SIZE };
        switch (tab) {
          case 'attendance':
            this.attendance.load({ ...query, ...paged, grouping, view: byMember ? 'Members' : 'Summary' });
            break;
          case 'occupancy':
            this.occupancy.load({ ...query, ...paged, activity });
            break;
          default:
            this.cancellations.load({ ...query, ...paged });
            break;
        }
      });
    });

    effect(() => {
      this.tab.current();
      this.filters.query();
      untracked(() => this.page.set(1));
    });
  }

  protected visitPoints(a: AttendanceReport): { label: string; value: number }[] {
    return a.series.map((p) => ({ label: p.start, value: p.visits }));
  }

  /** Solo las horas con entradas: un día tiene 24 y mostrar las vacías es ruido. */
  protected busyHours(a: AttendanceReport) {
    return a.byHour.filter((h) => h.visits > 0);
  }

  protected share(value: number, max: number): number {
    return max > 0 ? Math.round((value * 100) / max) : 0;
  }

  protected when(instant: string, zone: string): string {
    return this.format.dateTimeIn(instant, zone);
  }

  protected sessionTone(status: string): 'ok' | 'warn' | 'danger' | 'info' | 'off' {
    return status === 'Completed' ? 'ok' : status === 'Cancelled' ? 'danger' : status === 'PendingClosure' ? 'warn' : 'info';
  }
}
