import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import type { Observable } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { ClassAttendance, ClassReservation, MyClassSession } from '../../core/api/models';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Badge, type Tone } from '../../shared/badge';
import { AttemptKey } from '../../shared/idempotency';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';

type Tab = 'agenda' | 'reservations' | 'history';

const statusTone = (status: string): Tone =>
  status === 'Confirmed' ? 'ok' : status === 'Offered' ? 'warn' : status === 'Waitlisted' ? 'info' : 'off';

/**
 * «Mis clases» del portal del alumno: agenda de su sucursal para reservar o anotarse en la espera, sus reservas (cancelar, aceptar o
 * rechazar un cupo ofrecido) y su historial de asistencia. Todo sale de los endpoints «de mí»: el alumno nunca elige de quién son los datos.
 */
@Component({
  selector: 'fc-my-classes',
  imports: [TranslocoPipe, Badge, ReasonDialog],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="section" aria-labelledby="my-classes-title">
      <h2 id="my-classes-title">{{ 'myClasses.title' | transloco }}</h2>
      <p class="muted">{{ 'myClasses.branchTime' | transloco: { zone: timeZone() } }}</p>

      <div class="segmented" role="tablist">
        @for (t of tabs; track t) {
          <button type="button" role="tab" class="btn" [class.active]="tab() === t" [attr.aria-selected]="tab() === t" (click)="open(t)">
            {{ 'myClasses.tabs.' + t | transloco }}
          </button>
        }
      </div>

      @if (error()) {
        <p class="alert alert-danger" role="alert">{{ error() }}</p>
      }

      @switch (tab()) {
        @case ('agenda') {
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'schedule.when' | transloco }}</th>
                  <th>{{ 'schedule.class' | transloco }}</th>
                  <th>{{ 'myClasses.seats' | transloco }}</th>
                  <th>{{ 'common.status' | transloco }}</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (item of agenda(); track item.session.id) {
                  <tr>
                    <td>{{ when(item.session.startsAt) }}</td>
                    <td>{{ item.session.className }}<br /><small class="muted">{{ item.session.instructorName }}</small></td>
                    <td>{{ item.session.bookedCount }} / {{ item.session.capacity }}</td>
                    <td>
                      @if (item.reservationStatus) {
                        <fc-badge [tone]="tone(item.reservationStatus)">{{ 'enums.reservationStatus.' + item.reservationStatus | transloco }}</fc-badge>
                      } @else if (isFull(item)) {
                        <fc-badge tone="warn">{{ 'myClasses.full' | transloco }}</fc-badge>
                      }
                    </td>
                    <td class="row-actions">
                      @if (!item.reservationStatus) {
                        <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="reserve(item)">
                          {{ (isFull(item) ? 'myClasses.joinWaitlist' : 'myClasses.reserve') | transloco }}
                        </button>
                      } @else if (item.reservationStatus === 'Offered') {
                        <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="respond(item.reservationId!, true)">{{ 'myClasses.accept' | transloco }}</button>
                        <button type="button" class="btn" [disabled]="busy()" (click)="respond(item.reservationId!, false)">{{ 'myClasses.decline' | transloco }}</button>
                      } @else {
                        <button type="button" class="btn" [disabled]="busy()" (click)="askCancel(item.reservationId!, item.session.className)">{{ 'myClasses.cancel' | transloco }}</button>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="5" class="empty">{{ (loading() ? 'common.loading' : 'myClasses.emptyAgenda') | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        }
        @case ('reservations') {
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'schedule.when' | transloco }}</th>
                  <th>{{ 'schedule.class' | transloco }}</th>
                  <th>{{ 'common.status' | transloco }}</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                @for (r of reservations(); track r.id) {
                  <tr>
                    <td>{{ when(r.startsAt) }}</td>
                    <td>{{ r.className }}<br /><small class="muted">{{ r.instructorName }}</small></td>
                    <td>
                      <fc-badge [tone]="tone(r.status)">{{ 'enums.reservationStatus.' + r.status | transloco }}</fc-badge>
                      @if (r.status === 'Waitlisted' && r.waitlistPosition) {
                        <small class="muted"> · {{ 'myClasses.position' | transloco: { n: r.waitlistPosition } }}</small>
                      }
                      @if (r.status === 'Offered' && r.offerExpiresAt) {
                        <small class="muted"> · {{ 'myClasses.respondBy' | transloco: { time: when(r.offerExpiresAt) } }}</small>
                      }
                    </td>
                    <td class="row-actions">
                      @if (r.status === 'Offered') {
                        <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="respond(r.id, true)">{{ 'myClasses.accept' | transloco }}</button>
                        <button type="button" class="btn" [disabled]="busy()" (click)="respond(r.id, false)">{{ 'myClasses.decline' | transloco }}</button>
                      } @else if (r.status === 'Confirmed' || r.status === 'Waitlisted') {
                        <button type="button" class="btn" [disabled]="busy()" (click)="askCancel(r.id, r.className)">{{ 'myClasses.cancel' | transloco }}</button>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="4" class="empty">{{ (loading() ? 'common.loading' : 'myClasses.emptyReservations') | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        }
        @case ('history') {
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'schedule.when' | transloco }}</th>
                  <th>{{ 'schedule.class' | transloco }}</th>
                  <th>{{ 'classAttendance.outcome' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (a of history(); track a.id) {
                  <tr>
                    <td>{{ when(a.startsAt) }}</td>
                    <td>{{ a.className }}<br /><small class="muted">{{ a.instructorName }}</small></td>
                    <td><fc-badge [tone]="a.outcome === 'Attended' ? 'ok' : 'danger'">{{ 'enums.attendanceOutcome.' + a.outcome | transloco }}</fc-badge></td>
                  </tr>
                } @empty {
                  <tr><td colspan="3" class="empty">{{ (loading() ? 'common.loading' : 'myClasses.emptyHistory') | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        }
      }
    </section>

    @if (cancelling(); as target) {
      <fc-reason-dialog
        [title]="'myClasses.cancelTitle' | transloco"
        [label]="'common.reason' | transloco"
        [message]="target.name"
        [hint]="'myClasses.lateReasonHint' | transloco"
        [confirmLabel]="'myClasses.cancelTitle' | transloco"
        [minLength]="0"
        [danger]="true"
        [busy]="busy()"
        [error]="dialogError()"
        (submitted)="cancel(target.id, $event)"
        (cancelled)="cancelling.set(null)"
      />
    }
  `,
})
export class MyClasses implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly format = inject(Formatter);
  private readonly destroyRef = inject(DestroyRef);

  /** Zona horaria de la sucursal del alumno: todas las horas se muestran en ella. */
  readonly timeZone = input.required<string>();

  protected readonly tabs: Tab[] = ['agenda', 'reservations', 'history'];
  protected readonly tab = signal<Tab>('agenda');
  protected readonly agenda = signal<MyClassSession[]>([]);
  protected readonly reservations = signal<ClassReservation[]>([]);
  protected readonly history = signal<ClassAttendance[]>([]);
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly cancelling = signal<{ id: string; name: string } | null>(null);
  protected readonly tone = statusTone;

  private readonly reserveKey = new AttemptKey();

  ngOnInit(): void {
    this.load();
  }

  protected open(tab: Tab): void {
    this.tab.set(tab);
    this.load();
  }

  protected when(iso: string): string {
    return this.format.dateTimeIn(iso, this.timeZone());
  }

  protected isFull(item: MyClassSession): boolean {
    return item.session.bookedCount >= item.session.capacity;
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    const failed = (err: unknown): void => {
      this.loading.set(false);
      this.error.set(this.errors.message(err));
    };
    const loaded = <T>(target: { set(value: T[]): void }) => (page: { items: T[] }): void => {
      target.set(page.items);
      this.loading.set(false);
    };
    switch (this.tab()) {
      case 'agenda':
        this.api.mySessions({ pageSize: 50 }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: loaded(this.agenda), error: failed });
        break;
      case 'reservations':
        this.api.myClassReservations({ upcomingOnly: true, pageSize: 50 }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: loaded(this.reservations), error: failed });
        break;
      case 'history':
        this.api.myClassAttendance({ pageSize: 50 }).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: loaded(this.history), error: failed });
        break;
    }
  }

  private run<T>(call: Observable<T>, toastKey: string, onError: (message: string) => void, after?: () => void): void {
    this.busy.set(true);
    call.pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success(toastKey);
        after?.();
        this.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.reserveKey.afterError(err);
        onError(this.errors.message(err));
      },
    });
  }

  /** Reservar; si la sesión está llena, anota en la lista de espera (solo porque el alumno pulsa ese botón). */
  protected reserve(item: MyClassSession): void {
    const waitlist = this.isFull(item);
    this.run(
      this.api.reserveMyClass({ sessionId: item.session.id, joinWaitlist: waitlist }, this.reserveKey.value),
      waitlist ? 'myClasses.waitlistedToast' : 'myClasses.reservedToast',
      (message) => this.error.set(message),
      () => this.reserveKey.renew(),
    );
  }

  protected respond(reservationId: string, accept: boolean): void {
    this.run(
      accept ? this.api.acceptMyClassOffer(reservationId) : this.api.declineMyClassOffer(reservationId),
      accept ? 'myClasses.acceptedToast' : 'myClasses.declinedToast',
      (message) => this.error.set(message),
    );
  }

  protected askCancel(id: string, name: string): void {
    this.dialogError.set(null);
    this.cancelling.set({ id, name });
  }

  protected cancel(id: string, reason: string): void {
    this.run(
      this.api.cancelMyClassReservation(id, reason || null),
      'myClasses.cancelledToast',
      (message) => this.dialogError.set(message),
      () => this.cancelling.set(null),
    );
  }
}
