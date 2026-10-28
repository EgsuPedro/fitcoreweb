import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { forkJoin } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { MyAttendance, MyMember, MyMembership, MyPayment } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, membershipTone } from '../../shared/badge';
import { FcDate, FcDateTime, FcMoney } from '../../shared/format.pipes';
import { MyClasses } from './my-classes';
import { MemberWellness } from '../wellness/member-wellness';

/**
 * Resumen del alumno: su membresía, pagos y visitas. Todo viene de los endpoints «de mí», que resuelven al alumno desde su sesión.
 * Rutinas, nutrición, progreso y fotos vienen del componente de bienestar (mismas reglas de acceso que la API).
 */
@Component({
  selector: 'fc-my-portal',
  imports: [TranslocoPipe, Badge, FcDate, FcDateTime, FcMoney, MemberWellness, MyClasses],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    } @else if (member(); as m) {
      <header class="page-header">
        <div>
          <h1>{{ 'portal.hello' | transloco: { name: m.firstName } }}</h1>
          <p class="muted">{{ m.code }} · {{ m.branchName }}</p>
        </div>
      </header>

      <fc-my-classes [timeZone]="m.branchTimeZone" />

      <fc-member-wellness [memberId]="m.id" [self]="true" />

      <section class="section">
        <h2>{{ 'portal.memberships' | transloco }}</h2>
        <div class="card table-card">
          <table class="table">
            <thead>
              <tr>
                <th>{{ 'memberships.plan' | transloco }}</th>
                <th>{{ 'memberships.start' | transloco }}</th>
                <th>{{ 'memberships.end' | transloco }}</th>
                <th class="num">{{ 'memberships.balance' | transloco }}</th>
                <th>{{ 'common.status' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (ms of memberships(); track ms.id) {
                <tr>
                  <td>{{ ms.planName }}</td>
                  <td>{{ ms.startDate | fcDate }}</td>
                  <td>{{ ms.endDate | fcDate }}</td>
                  <td class="num">{{ ms.balance | fcMoney }}</td>
                  <td><fc-badge [tone]="tone(ms.status)">{{ 'enums.membershipStatus.' + ms.status | transloco }}</fc-badge></td>
                </tr>
              } @empty {
                <tr><td colspan="5" class="empty">{{ 'portal.noMemberships' | transloco }}</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>

      <div class="grid-2">
        <section class="section">
          <h2>{{ 'portal.payments' | transloco }}</h2>
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'billing.payments.receipt' | transloco }}</th>
                  <th>{{ 'billing.payments.date' | transloco }}</th>
                  <th class="num">{{ 'billing.payments.amount' | transloco }}</th>
                  <th>{{ 'billing.payments.status' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (p of payments(); track p.id) {
                  <tr>
                    <td>{{ p.receiptNumber }}</td>
                    <td>{{ p.paidAt | fcDateTime }}</td>
                    <td class="num">{{ p.amount | fcMoney }}</td>
                    <td>{{ 'enums.paymentStatus.' + p.status | transloco }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="4" class="empty">{{ 'portal.noPayments' | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>

        <section class="section">
          <h2>{{ 'portal.attendance' | transloco }}</h2>
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'attendance.in' | transloco }}</th>
                  <th>{{ 'attendance.out' | transloco }}</th>
                  <th>{{ 'common.branch' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (a of visits(); track a.id) {
                  <tr>
                    <td>{{ a.checkInAt | fcDateTime }}</td>
                    <td>{{ a.checkOutAt | fcDateTime }}</td>
                    <td>{{ a.branchName }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="3" class="empty">{{ 'portal.noAttendance' | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      </div>
    } @else {
      <p class="muted">{{ 'common.loading' | transloco }}</p>
    }
  `,
})
export class MyPortal implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);

  protected readonly member = signal<MyMember | null>(null);
  protected readonly memberships = signal<MyMembership[]>([]);
  protected readonly payments = signal<MyPayment[]>([]);
  protected readonly visits = signal<MyAttendance[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly tone = membershipTone;

  ngOnInit(): void {
    forkJoin({
      member: this.api.myMember(),
      memberships: this.api.myMemberships({ pageSize: 10 }),
      payments: this.api.myPayments({ pageSize: 10 }),
      attendance: this.api.myAttendance({ pageSize: 10 }),
    }).subscribe({
      next: (data) => {
        this.member.set(data.member);
        this.memberships.set(data.memberships.items);
        this.payments.set(data.payments.items);
        this.visits.set(data.attendance.items);
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }
}
