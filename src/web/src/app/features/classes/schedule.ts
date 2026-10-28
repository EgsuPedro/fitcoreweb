import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { ClassSession, GroupClass, Trainer } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { BranchCatalog } from '../../shared/branch-catalog';
import { DEFAULT_TIME_ZONE, addDays, dateKeyOf, timeKeyOf, toInstant } from '../../shared/zoned-time';
import { CalendarView, datesBetween, groupByDate, monthGrid, rangeOf, shift } from './calendar-math';
import { SeriesDialog } from './series-dialog';
import { SessionForm } from './session-form';
import { SessionPanel } from './session-panel';

const VIEWS: readonly CalendarView[] = ['day', 'week', 'month'];
const MAX_PAGES = 10;

/**
 * Horario de las clases grupales en día, semana o mes. Todo se muestra en la hora de la sucursal (no en la del navegador). Desde aquí
 * se programan sesiones y series y se abre cada sesión para reservar, atender la lista de espera y registrar la asistencia.
 */
@Component({
  selector: 'fc-schedule',
  imports: [TranslocoPipe, SessionForm, SessionPanel, SeriesDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'schedule.title' | transloco }}</h1>
      <span class="header-actions">
        <button type="button" class="btn" (click)="seriesOpen.set(true)">{{ 'schedule.seriesTitle' | transloco }}</button>
        @if (canSchedule()) {
          <button type="button" class="btn btn-primary" (click)="creating.set(true)">{{ 'schedule.new' | transloco }}</button>
        }
      </span>
    </header>

    <div class="toolbar cal-toolbar">
      <span class="segmented" role="group" [attr.aria-label]="'schedule.view' | transloco">
        @for (v of views; track v) {
          <button type="button" class="btn" [class.active]="view() === v" [attr.aria-pressed]="view() === v" (click)="setView(v)">
            {{ 'schedule.views.' + v | transloco }}
          </button>
        }
      </span>
      <button type="button" class="btn" [attr.aria-label]="'schedule.previous' | transloco" (click)="move(-1)">‹</button>
      <button type="button" class="btn" (click)="today()">{{ 'schedule.today' | transloco }}</button>
      <button type="button" class="btn" [attr.aria-label]="'schedule.next' | transloco" (click)="move(1)">›</button>
      <strong class="cal-title" aria-live="polite">{{ title() }}</strong>
    </div>

    <div class="toolbar">
      <select [attr.aria-label]="'common.branch' | transloco" (change)="setBranch($any($event.target).value)">
        <option value="">{{ 'common.allBranches' | transloco }}</option>
        @for (branch of catalog.branches(); track branch.id) {
          <option [value]="branch.id" [selected]="branch.id === branchId()">{{ branch.name }}</option>
        }
      </select>
      <select [attr.aria-label]="'classes.instructor' | transloco" (change)="setInstructor($any($event.target).value)">
        <option value="">{{ 'schedule.allInstructors' | transloco }}</option>
        @for (t of instructors(); track t.id) {
          <option [value]="t.id">{{ t.fullName }}</option>
        }
      </select>
      <select [attr.aria-label]="'schedule.class' | transloco" (change)="setClass($any($event.target).value)">
        <option value="">{{ 'schedule.allClasses' | transloco }}</option>
        @for (c of classes(); track c.id) {
          <option [value]="c.id">{{ c.name }}</option>
        }
      </select>
      <span class="muted">{{ 'schedule.branchTime' | transloco: { zone: timeZone() } }}</span>
    </div>

    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }
    @if (loading()) {
      <p class="muted" role="status">{{ 'common.loading' | transloco }}</p>
    }

    @if (view() === 'month') {
      <div class="cal-month" role="grid" [attr.aria-label]="title()">
        <div class="cal-weekdays" role="row">
          @for (d of weekHeader(); track d) {
            <span role="columnheader">{{ format.weekdayShort(d) }}</span>
          }
        </div>
        @for (week of grid(); track week[0]) {
          <div class="cal-week-row" role="row">
            @for (day of week; track day) {
              <div class="cal-cell" role="gridcell" [class.today]="day === todayKey()" [class.outside]="!inMonth(day)">
                <button type="button" class="cal-daynum" (click)="openDay(day)" [attr.aria-label]="format.dayHeading(day)">{{ day.slice(8) }}</button>
                @for (s of sessionsOn(day).slice(0, 3); track s.id) {
                  <button type="button" class="cal-session compact" [class]="stateClass(s)" (click)="selected.set(s)">
                    {{ time(s) }} {{ s.className }}
                  </button>
                }
                @if (sessionsOn(day).length > 3) {
                  <button type="button" class="cal-more" (click)="openDay(day)">{{ 'schedule.more' | transloco: { count: sessionsOn(day).length - 3 } }}</button>
                }
              </div>
            }
          </div>
        }
      </div>
    } @else {
      <div class="cal-days" [class.single]="view() === 'day'">
        @for (day of days(); track day) {
          <section class="cal-day" [class.today]="day === todayKey()" [attr.aria-label]="format.dayHeading(day)">
            <h3>{{ format.dayHeading(day) }}</h3>
            @for (s of sessionsOn(day); track s.id) {
              <button type="button" class="cal-session" [class]="stateClass(s)" (click)="selected.set(s)">
                <span class="cal-time">{{ time(s) }}–{{ endTime(s) }}</span>
                <strong>{{ s.className }}</strong>
                <small>{{ s.instructorName }}</small>
                <small>
                  {{ s.bookedCount }}/{{ s.capacity }}
                  @if (s.waitlistCount > 0) {
                    · {{ 'schedule.waiting' | transloco: { count: s.waitlistCount } }}
                  }
                  @if (s.status !== 'Scheduled') {
                    · {{ 'enums.sessionStatus.' + s.status | transloco }}
                  }
                </small>
              </button>
            } @empty {
              <p class="muted cal-empty">{{ 'schedule.noSessions' | transloco }}</p>
            }
          </section>
        }
      </div>
    }

    @if (selected(); as s) {
      <fc-session-panel [session]="s" [timeZone]="timeZone()" (changed)="load()" (closed)="selected.set(null)" />
    }
    @if (creating()) {
      <fc-session-form [initialDate]="formDate()" [branchId]="branchId()" (saved)="load()" (closed)="creating.set(false)" />
    }
    @if (seriesOpen()) {
      <fc-series-dialog [branchId]="branchId()" (changed)="load()" (closed)="seriesOpen.set(false)" />
    }
  `,
})
export class Schedule implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  protected readonly catalog = inject(BranchCatalog);
  protected readonly format = inject(Formatter);

  protected readonly views = VIEWS;
  protected readonly view = signal<CalendarView>('week');
  protected readonly branchId = signal('');
  protected readonly instructorId = signal('');
  protected readonly classId = signal('');
  protected readonly sessions = signal<ClassSession[]>([]);
  protected readonly instructors = signal<Trainer[]>([]);
  protected readonly classes = signal<GroupClass[]>([]);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly selected = signal<ClassSession | null>(null);
  protected readonly creating = signal(false);
  protected readonly seriesOpen = signal(false);
  protected readonly canSchedule = computed(() => this.session.has(Perm.ClassSessionsManage));

  /** La zona horaria de la sucursal elegida; sin elegir, la de la primera a la que el usuario tiene acceso. */
  protected readonly timeZone = computed(() => {
    const branches = this.catalog.branches();
    return (branches.find((b) => b.id === this.branchId()) ?? branches[0])?.timeZone ?? DEFAULT_TIME_ZONE;
  });
  protected readonly todayKey = computed(() => dateKeyOf(new Date(), this.timeZone()));
  /** Día de referencia de la vista (en la hora de la sucursal). */
  protected readonly anchor = signal(dateKeyOf(new Date(), DEFAULT_TIME_ZONE));
  protected readonly range = computed(() => rangeOf(this.view(), this.anchor()));
  protected readonly days = computed(() => datesBetween(this.range().from, this.range().to));
  protected readonly grid = computed(() => monthGrid(this.anchor()));
  protected readonly weekHeader = computed(() => (this.grid()[0] ?? []));
  protected readonly byDate = computed(() => groupByDate(this.sessions(), (s) => dateKeyOf(s.startsAt, this.timeZone())));
  protected readonly formDate = computed(() => (this.view() === 'day' ? this.anchor() : this.todayKey()));
  protected readonly title = computed(() => {
    const { from, to } = this.range();
    switch (this.view()) {
      case 'day':
        return this.format.dayHeading(this.anchor());
      case 'week':
        return `${this.format.dateOnly(from)} – ${this.format.dateOnly(addDays(to, -1))}`;
      default:
        return this.format.monthTitle(this.anchor());
    }
  });

  ngOnInit(): void {
    this.catalog.ensure();
    // Un usuario de una sola sucursal ve la suya sin tener que elegirla.
    this.anchor.set(this.todayKey());
    this.loadFilters();
    this.load();
  }

  protected sessionsOn(day: string): ClassSession[] {
    return this.byDate().get(day) ?? [];
  }

  protected inMonth(day: string): boolean {
    return day.slice(0, 7) === this.anchor().slice(0, 7);
  }

  protected time(s: ClassSession): string {
    return timeKeyOf(s.startsAt, this.timeZone());
  }

  protected endTime(s: ClassSession): string {
    return timeKeyOf(s.endsAt, this.timeZone());
  }

  protected stateClass(s: ClassSession): string {
    const full = s.status === 'Scheduled' && s.bookedCount >= s.capacity;
    return s.status === 'Cancelled' ? 'is-cancelled' : s.status === 'Completed' ? 'is-completed' : full ? 'is-full' : '';
  }

  protected setView(view: CalendarView): void {
    this.view.set(view);
    this.load();
  }

  protected move(direction: -1 | 1): void {
    this.anchor.set(shift(this.view(), this.anchor(), direction));
    this.load();
  }

  protected today(): void {
    this.anchor.set(this.todayKey());
    this.load();
  }

  protected openDay(day: string): void {
    this.anchor.set(day);
    this.view.set('day');
    this.load();
  }

  protected setBranch(id: string): void {
    this.branchId.set(id);
    this.instructorId.set('');
    this.classId.set('');
    this.loadFilters();
    this.load();
  }

  protected setInstructor(id: string): void {
    this.instructorId.set(id);
    this.load();
  }

  protected setClass(id: string): void {
    this.classId.set(id);
    this.load();
  }

  private loadFilters(): void {
    const branchId = this.branchId();
    this.api.trainers({ pageSize: 100, status: 'Active', branchId }).subscribe({ next: (page) => this.instructors.set(page.items) });
    this.api.groupClasses({ pageSize: 100, branchId }).subscribe({ next: (page) => this.classes.set(page.items) });
  }

  private requestId = 0;

  /** Carga todas las sesiones del rango (varias páginas si hace falta); una respuesta vieja nunca pisa a una más reciente. */
  protected load(): void {
    const id = ++this.requestId;
    const { from, to } = this.range();
    const zone = this.timeZone();
    const base = {
      from: toInstant(from, '00:00', zone),
      to: toInstant(to, '00:00', zone),
      branchId: this.branchId(),
      instructorId: this.instructorId(),
      classId: this.classId(),
      pageSize: 100,
    };
    this.loading.set(true);
    this.error.set(null);
    const collected: ClassSession[] = [];
    const next = (page: number): void => {
      this.api.classSessions({ ...base, page }).subscribe({
        next: (result) => {
          if (id !== this.requestId) {
            return;
          }
          collected.push(...result.items);
          if (collected.length < result.totalCount && page < MAX_PAGES && result.items.length > 0) {
            next(page + 1);
            return;
          }
          this.sessions.set(collected);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          if (id === this.requestId) {
            this.loading.set(false);
            this.error.set(this.errors.message(err));
          }
        },
      });
    };
    next(1);
  }
}
