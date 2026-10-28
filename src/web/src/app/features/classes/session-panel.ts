import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import type { Observable } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { ClassRoster, ClassSession, Member, RosterEntry, Trainer } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Badge, Tone } from '../../shared/badge';
import { AttemptKey } from '../../shared/idempotency';
import { MemberPicker } from '../../shared/member-picker';
import { Modal } from '../../shared/modal';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';
import { dateKeyOf, timeKeyOf, toInstant } from '../../shared/zoned-time';

type Tab = 'participants' | 'waitlist' | 'reserve';
type Dialog = 'edit' | 'cancel' | 'complete' | null;

export const sessionTone = (status: string): Tone => (status === 'Scheduled' ? 'ok' : status === 'Completed' ? 'info' : 'off');

/**
 * Una sesión del horario: quién tiene cupo, quién espera, la asistencia y las acciones del personal. Cada botón aparece solo con el
 * permiso que le corresponde, y todo lo que se muestra viene del servidor (nada se da por hecho antes de que responda).
 */
@Component({
  selector: 'fc-session-panel',
  imports: [ReactiveFormsModule, TranslocoPipe, Badge, Modal, ReasonDialog, MemberPicker],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="session().className" (closed)="closed.emit()">
      @if (error()) {
        <p class="alert alert-danger" role="alert">{{ error() }}</p>
      }

      <dl class="summary">
        <dt>{{ 'schedule.when' | transloco }}</dt>
        <dd>{{ format.dateTimeIn(session().startsAt, timeZone()) }} – {{ format.timeIn(session().endsAt, timeZone()) }}</dd>
        <dt>{{ 'classes.instructor' | transloco }}</dt>
        <dd>{{ session().instructorName }}</dd>
        <dt>{{ 'common.branch' | transloco }}</dt>
        <dd>{{ session().branchName }}</dd>
        <dt>{{ 'common.status' | transloco }}</dt>
        <dd>
          <fc-badge [tone]="tone(session().status)">{{ 'enums.sessionStatus.' + session().status | transloco }}</fc-badge>
          @if (session().isModified) {
            <fc-badge tone="warn">{{ 'schedule.modified' | transloco }}</fc-badge>
          }
        </dd>
        <dt>{{ 'schedule.occupancy' | transloco }}</dt>
        <dd>
          {{ 'schedule.occupancyValue' | transloco: { booked: session().bookedCount, capacity: session().capacity } }}
          @if (session().waitlistCount > 0) {
            · {{ 'schedule.waiting' | transloco: { count: session().waitlistCount } }}
          }
        </dd>
        @if (session().notes) {
          <dt>{{ 'common.notes' | transloco }}</dt>
          <dd>{{ session().notes }}</dd>
        }
        @if (session().availabilityOverrideReason) {
          <dt>{{ 'schedule.exception' | transloco }}</dt>
          <dd>{{ session().availabilityOverrideReason }}</dd>
        }
        @if (session().cancelReason) {
          <dt>{{ 'schedule.cancelReason' | transloco }}</dt>
          <dd>{{ session().cancelReason }}</dd>
        }
      </dl>

      <div class="shortcuts">
        @if (canEdit()) {
          <button type="button" class="btn" (click)="dialog.set('edit'); openEdit()">{{ 'schedule.edit' | transloco }}</button>
        }
        @if (canCancelSession()) {
          <button type="button" class="btn btn-danger" (click)="dialog.set('cancel')">{{ 'schedule.cancelSession' | transloco }}</button>
        }
        @if (canComplete()) {
          <button type="button" class="btn" (click)="dialog.set('complete')">{{ 'schedule.complete' | transloco }}</button>
        }
      </div>

      @if (canRead()) {
        <div class="tabs" role="tablist" [attr.aria-label]="'schedule.participants' | transloco">
          @for (t of tabs(); track t.id) {
            <button type="button" role="tab" class="tab" [class.active]="tab() === t.id" [attr.aria-selected]="tab() === t.id" (click)="tab.set(t.id)">
              {{ t.labelKey | transloco }}
            </button>
          }
        </div>

        @if (roster(); as r) {
          @switch (tab()) {
            @case ('participants') {
              <p class="muted">{{ 'schedule.rosterSummary' | transloco: { confirmed: r.confirmed, attended: r.attended, noShow: r.noShow, pending: r.pending } }}</p>
              @if (canRecord() && r.pending > 0) {
                <button type="button" class="btn" [disabled]="busy()" (click)="markAllAttended(r)">{{ 'schedule.markAllAttended' | transloco }}</button>
              }
              <div class="card table-card">
                <table class="table">
                  <thead>
                    <tr>
                      <th>{{ 'schedule.member' | transloco }}</th>
                      <th>{{ 'schedule.attendance' | transloco }}</th>
                      <th></th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (p of r.participants; track p.reservationId) {
                      <tr>
                        <td>{{ p.memberName }}<br /><small class="muted">{{ p.memberCode }}</small></td>
                        <td>
                          @if (p.outcome) {
                            <fc-badge [tone]="p.outcome === 'Attended' ? 'ok' : 'danger'">{{ 'enums.attendanceOutcome.' + p.outcome | transloco }}</fc-badge>
                            @if (p.hasGymEntry) {
                              <small class="muted"> · {{ 'schedule.gymEntry' | transloco }}</small>
                            }
                          } @else {
                            <span class="muted">{{ 'schedule.pending' | transloco }}</span>
                          }
                        </td>
                        <td class="actions">
                          @if (canRecord()) {
                            <button type="button" class="btn" [disabled]="busy() || p.outcome === 'Attended'" (click)="record(p, 'Attended')">{{ 'enums.attendanceOutcome.Attended' | transloco }}</button>
                            <button type="button" class="btn" [disabled]="busy() || p.outcome === 'NoShow'" (click)="record(p, 'NoShow')">{{ 'enums.attendanceOutcome.NoShow' | transloco }}</button>
                          }
                          @if (canReserve() && !p.outcome && !started()) {
                            <button type="button" class="btn btn-danger" (click)="cancelling.set(p)">{{ 'schedule.cancelReservation' | transloco }}</button>
                          }
                        </td>
                      </tr>
                    } @empty {
                      <tr><td colspan="3" class="empty">{{ 'schedule.noParticipants' | transloco }}</td></tr>
                    }
                  </tbody>
                </table>
              </div>
            }
            @case ('waitlist') {
              <h3>{{ 'schedule.offers' | transloco }}</h3>
              <div class="card table-card">
                <table class="table">
                  <tbody>
                    @for (o of r.offers; track o.reservationId) {
                      <tr>
                        <td>{{ o.memberName }}<br /><small class="muted">{{ o.memberCode }}</small></td>
                        <td>
                          @if (o.offerExpiresAt) {
                            {{ 'schedule.offerUntil' | transloco: { time: format.timeIn(o.offerExpiresAt, timeZone()) } }}
                          }
                        </td>
                        <td class="actions">
                          @if (canReserve()) {
                            <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="respond(o, true)">{{ 'schedule.acceptOffer' | transloco }}</button>
                            <button type="button" class="btn" [disabled]="busy()" (click)="respond(o, false)">{{ 'schedule.declineOffer' | transloco }}</button>
                          }
                        </td>
                      </tr>
                    } @empty {
                      <tr><td colspan="3" class="empty">{{ 'schedule.noOffers' | transloco }}</td></tr>
                    }
                  </tbody>
                </table>
              </div>
              <h3>{{ 'schedule.queue' | transloco }}</h3>
              <div class="card table-card">
                <table class="table">
                  <tbody>
                    @for (w of r.waitlist; track w.reservationId) {
                      <tr>
                        <td class="num">{{ w.waitlistPosition }}</td>
                        <td>{{ w.memberName }}<br /><small class="muted">{{ w.memberCode }}</small></td>
                        <td class="actions">
                          @if (canReserve()) {
                            <button type="button" class="btn" (click)="cancelling.set(w)">{{ 'schedule.leaveQueue' | transloco }}</button>
                          }
                        </td>
                      </tr>
                    } @empty {
                      <tr><td colspan="3" class="empty">{{ 'schedule.emptyQueue' | transloco }}</td></tr>
                    }
                  </tbody>
                </table>
              </div>
            }
            @case ('reserve') {
              @if (session().status !== 'Scheduled' || started()) {
                <p class="alert alert-warning">{{ 'schedule.notBookable' | transloco }}</p>
              } @else {
                <fc-member-picker (picked)="member.set($event)" />
                <label class="check">
                  <input type="checkbox" [checked]="joinWaitlist()" (change)="joinWaitlist.set($any($event.target).checked)" />
                  <span>{{ 'schedule.joinWaitlist' | transloco }}</span>
                </label>
                <button type="button" class="btn btn-primary" [disabled]="!member() || busy()" (click)="reserve()">{{ 'schedule.reserve' | transloco }}</button>
              }
            }
          }
        } @else {
          <p class="muted">{{ 'common.loading' | transloco }}</p>
        }
      }

      <ng-container footer>
        <button type="button" class="btn" (click)="closed.emit()">{{ 'common.close' | transloco }}</button>
      </ng-container>
    </fc-modal>

    @switch (dialog()) {
      @case ('edit') {
        <fc-modal [title]="'schedule.edit' | transloco" (closed)="dialog.set(null)">
          <form id="edit-session-form" [formGroup]="editForm" (ngSubmit)="saveEdit()" novalidate>
            @if (dialogError()) {
              <p class="alert alert-danger" role="alert">{{ dialogError() }}</p>
            }
            <div class="grid-2">
              <label class="field">
                <span>{{ 'common.date' | transloco }}</span>
                <input type="date" formControlName="date" />
              </label>
              <label class="field">
                <span>{{ 'schedule.time' | transloco }}</span>
                <input type="time" formControlName="time" />
                <small class="muted">{{ 'schedule.branchTime' | transloco: { zone: timeZone() } }}</small>
              </label>
              <label class="field">
                <span>{{ 'classes.instructor' | transloco }}</span>
                <select formControlName="instructorId">
                  @for (t of instructors(); track t.id) {
                    <option [value]="t.id">{{ t.fullName }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span>{{ 'classes.capacity' | transloco }}</span>
                <input type="number" formControlName="capacity" min="1" max="500" />
                <small class="muted">{{ 'schedule.capacityHint' | transloco: { booked: session().bookedCount } }}</small>
              </label>
            </div>
            <label class="field">
              <span>{{ 'common.notes' | transloco }}</span>
              <input formControlName="notes" maxlength="500" />
            </label>
            <label class="field">
              <span>{{ 'schedule.overrideReason' | transloco }}</span>
              <input formControlName="overrideReason" maxlength="300" />
              <small class="muted">{{ 'schedule.overrideHint' | transloco }}</small>
            </label>
          </form>
          <ng-container footer>
            <button type="button" class="btn" (click)="dialog.set(null)">{{ 'common.cancel' | transloco }}</button>
            <button type="submit" form="edit-session-form" class="btn btn-primary" [disabled]="busy()">{{ 'common.save' | transloco }}</button>
          </ng-container>
        </fc-modal>
      }
      @case ('cancel') {
        <fc-reason-dialog
          [title]="'schedule.cancelSession' | transloco"
          [label]="'common.reason' | transloco"
          [message]="'schedule.cancelSessionWarning' | transloco: { count: session().bookedCount + session().waitlistCount }"
          [confirmLabel]="'schedule.cancelSession' | transloco"
          [minLength]="3"
          [danger]="true"
          [busy]="busy()"
          [error]="dialogError()"
          (submitted)="cancelSession($event)"
          (cancelled)="dialog.set(null)"
        />
      }
      @case ('complete') {
        <fc-modal [title]="'schedule.complete' | transloco" (closed)="dialog.set(null)">
          @if (dialogError()) {
            <p class="alert alert-danger" role="alert">{{ dialogError() }}</p>
          }
          <p>{{ 'schedule.completeHint' | transloco }}</p>
          <label class="check">
            <input type="checkbox" [checked]="markPending()" (change)="markPending.set($any($event.target).checked)" />
            <span>{{ 'schedule.markPending' | transloco: { count: roster()?.pending ?? 0 } }}</span>
          </label>
          <ng-container footer>
            <button type="button" class="btn" (click)="dialog.set(null)">{{ 'common.cancel' | transloco }}</button>
            <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="complete()">{{ 'schedule.complete' | transloco }}</button>
          </ng-container>
        </fc-modal>
      }
    }

    @if (cancelling(); as entry) {
      <fc-reason-dialog
        [title]="'schedule.cancelReservation' | transloco"
        [label]="'common.reason' | transloco"
        [message]="entry.memberName"
        [hint]="'schedule.lateReasonHint' | transloco"
        [confirmLabel]="'schedule.cancelReservation' | transloco"
        [minLength]="0"
        [danger]="true"
        [busy]="busy()"
        [error]="dialogError()"
        (submitted)="cancelReservation(entry, $event)"
        (cancelled)="cancelling.set(null)"
      />
    }
  `,
})
export class SessionPanel implements OnInit {
  private readonly api = inject(ApiService);
  private readonly sessionService = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);
  protected readonly format = inject(Formatter);

  readonly sessionInput = input.required<ClassSession>({ alias: 'session' });
  readonly timeZone = input.required<string>();
  readonly changed = output<void>();
  readonly closed = output<void>();

  protected readonly session = signal<ClassSession>(null as never);
  protected readonly roster = signal<ClassRoster | null>(null);
  protected readonly tab = signal<Tab>('participants');
  protected readonly dialog = signal<Dialog>(null);
  protected readonly cancelling = signal<RosterEntry | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly member = signal<Member | null>(null);
  protected readonly joinWaitlist = signal(false);
  protected readonly markPending = signal(false);
  protected readonly instructors = signal<Trainer[]>([]);
  private readonly reserveKey = new AttemptKey();

  protected readonly editForm = this.fb.nonNullable.group({
    date: [''],
    time: [''],
    instructorId: [''],
    capacity: [1],
    notes: [''],
    overrideReason: [''],
  });

  protected readonly canRead = computed(() => this.sessionService.has(Perm.ClassAttendanceRead));
  protected readonly canReserve = computed(() => this.sessionService.has(Perm.ClassReservationsManage));
  protected readonly canRecord = computed(() => this.sessionService.has(Perm.ClassAttendanceManage) && this.started() && this.session().status !== 'Cancelled');
  protected readonly started = computed(() => new Date(this.session().startsAt).getTime() <= Date.now());
  protected readonly canEdit = computed(() => this.sessionService.has(Perm.ClassSessionsManage) && this.session().status === 'Scheduled' && !this.started());
  protected readonly canCancelSession = computed(() => this.sessionService.has(Perm.ClassSessionsManage) && this.session().status === 'Scheduled' && new Date(this.session().endsAt).getTime() > Date.now());
  protected readonly canComplete = computed(() => this.sessionService.has(Perm.ClassAttendanceManage) && this.session().status === 'Scheduled' && this.started());
  protected readonly tabs = computed(() => {
    const all: { id: Tab; labelKey: string }[] = [
      { id: 'participants', labelKey: 'schedule.participants' },
      { id: 'waitlist', labelKey: 'schedule.waitlist' },
    ];
    if (this.canReserve()) {
      all.push({ id: 'reserve', labelKey: 'schedule.reserve' });
    }
    return all;
  });

  protected tone = sessionTone;

  ngOnInit(): void {
    this.session.set(this.sessionInput());
    this.loadRoster();
  }

  private loadRoster(): void {
    if (!this.canRead()) {
      return;
    }
    this.api.classRoster(this.session().id).subscribe({
      next: (roster) => this.roster.set(roster),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  /** Lo que cambia una acción: se vuelven a leer la sesión y la lista, y se avisa al calendario. */
  private refresh(): void {
    this.api.classSession(this.session().id).subscribe({ next: (fresh) => this.session.set(fresh) });
    this.loadRoster();
    this.changed.emit();
  }

  private run<T>(call: Observable<T>, toastKey: string, after?: (result: T) => void, onError?: (message: string) => void): void {
    this.busy.set(true);
    this.error.set(null);
    this.dialogError.set(null);
    call.subscribe({
      next: (result) => {
        this.busy.set(false);
        this.toast.success(toastKey);
        after?.(result);
        this.refresh();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        const message = this.errors.message(err);
        (onError ?? ((m) => this.error.set(m)))(message);
        this.reserveKey.afterError(err);
      },
    });
  }

  protected record(entry: RosterEntry, outcome: 'Attended' | 'NoShow'): void {
    this.run(this.api.recordClassAttendance(this.session().id, [{ reservationId: entry.reservationId, outcome, notes: null }]), 'schedule.attendanceSavedToast');
  }

  protected markAllAttended(roster: ClassRoster): void {
    const pending = roster.participants.filter((p) => !p.outcome).map((p) => ({ reservationId: p.reservationId, outcome: 'Attended' as const, notes: null }));
    this.run(this.api.recordClassAttendance(this.session().id, pending), 'schedule.attendanceSavedToast');
  }

  protected respond(entry: RosterEntry, accept: boolean): void {
    this.run(
      accept ? this.api.acceptClassOffer(entry.reservationId) : this.api.declineClassOffer(entry.reservationId),
      accept ? 'schedule.offerAcceptedToast' : 'schedule.offerDeclinedToast',
    );
  }

  protected reserve(): void {
    const member = this.member();
    if (!member) {
      return;
    }
    this.run(
      this.api.reserveClass({ sessionId: this.session().id, memberId: member.id, joinWaitlist: this.joinWaitlist() }, this.reserveKey.value),
      'schedule.reservedToast',
      () => {
        this.reserveKey.renew();
        this.member.set(null);
        this.tab.set('participants');
      },
    );
  }

  protected cancelReservation(entry: RosterEntry, reason: string): void {
    this.run(
      this.api.cancelClassReservation(entry.reservationId, reason || null),
      'schedule.reservationCancelledToast',
      () => this.cancelling.set(null),
      (message) => this.dialogError.set(message),
    );
  }

  protected cancelSession(reason: string): void {
    this.run(
      this.api.cancelClassSession(this.session().id, reason),
      'schedule.sessionCancelledToast',
      () => this.dialog.set(null),
      (message) => this.dialogError.set(message),
    );
  }

  protected complete(): void {
    this.run(
      this.api.completeClassSession(this.session().id, this.markPending()),
      'schedule.completedToast',
      () => this.dialog.set(null),
      (message) => this.dialogError.set(message),
    );
  }

  protected openEdit(): void {
    const s = this.session();
    this.dialogError.set(null);
    this.editForm.reset({
      date: dateKeyOf(s.startsAt, this.timeZone()),
      time: timeKeyOf(s.startsAt, this.timeZone()),
      instructorId: s.instructorId,
      capacity: s.capacity,
      notes: s.notes ?? '',
      overrideReason: '',
    });
    this.api.trainers({ pageSize: 100, status: 'Active', branchId: s.branchId }).subscribe((page) => this.instructors.set(page.items));
  }

  protected saveEdit(): void {
    const s = this.session();
    const v = this.editForm.getRawValue();
    const startsAt = toInstant(v.date, v.time, this.timeZone());
    const changed = {
      startsAt: new Date(startsAt).getTime() !== new Date(s.startsAt).getTime() ? startsAt : null,
      instructorId: v.instructorId && v.instructorId !== s.instructorId ? v.instructorId : null,
      capacity: Number(v.capacity) !== s.capacity ? Number(v.capacity) : null,
      // Vacío borra las notas; omitido las deja como están.
      notes: v.notes.trim() !== (s.notes ?? '') ? v.notes.trim() : null,
      availabilityOverrideReason: v.overrideReason.trim() || null,
    };
    if (changed.startsAt === null && changed.instructorId === null && changed.capacity === null && changed.notes === null) {
      this.dialogError.set(this.errors.text('schedule.nothingToChange'));
      return;
    }
    this.run(
      this.api.updateClassSession(s.id, changed),
      'schedule.updatedToast',
      () => this.dialog.set(null),
      (message) => this.dialogError.set(message),
    );
  }
}
