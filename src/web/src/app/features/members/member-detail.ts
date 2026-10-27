import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { switchMap } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { Attendance, Member, Membership, Obligation, Payment } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, membershipTone, obligationTone, paymentTone } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { Confirm } from '../../shared/confirm';
import { FcDate, FcDateTime, FcMoney } from '../../shared/format.pipes';
import { Modal } from '../../shared/modal';
import { ToastService } from '../../shared/toast';
import { PaymentDialog } from '../billing/payment-dialog';
import { ReceiptView } from '../billing/receipt-view';
import { EnrollDialog } from '../memberships/enroll-dialog';
import { MemberForm } from './member-form';

/** Ficha del alumno: datos, membresías, obligaciones, pagos, visitas y código QR. */
@Component({
  selector: 'fc-member-detail',
  imports: [RouterLink, TranslocoPipe, Badge, Modal, Confirm, FcDate, FcDateTime, FcMoney, MemberForm, EnrollDialog, PaymentDialog, ReceiptView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
      <a routerLink="/members" class="btn">{{ 'common.back' | transloco }}</a>
    } @else if (member(); as m) {
      <header class="page-header">
        <div>
          <h1>{{ m.firstName }} {{ m.lastName }}</h1>
          <p class="muted">
            {{ m.code }} · {{ 'enums.idType.' + m.idType | transloco }} {{ m.idNumber }} · {{ catalog.nameOf(m.branchId) }}
            <fc-badge [tone]="m.status === 'Active' ? 'ok' : 'off'">{{ 'enums.memberStatus.' + m.status | transloco }}</fc-badge>
          </p>
        </div>
      </header>

      <div class="inline-actions">
        @if (can(perm.MembersUpdate)) {
          <button type="button" class="btn" (click)="dialog.set('edit')">{{ 'common.edit' | transloco }}</button>
        }
        @if (can(perm.MembersStatus)) {
          <button type="button" class="btn" (click)="dialog.set('status')">
            {{ (m.status === 'Active' ? 'members.deactivate' : 'members.activate') | transloco }}
          </button>
        }
        @if (can(perm.MembershipsCreate) && m.status === 'Active') {
          <button type="button" class="btn btn-primary" (click)="dialog.set('enroll')">{{ 'memberships.new' | transloco }}</button>
        }
        @if (can(perm.PaymentsCreate)) {
          <button type="button" class="btn btn-primary" (click)="openPayment(null)">{{ 'billing.payments.new' | transloco }}</button>
        }
      </div>

      <div class="grid-2">
        <section class="card section">
          <h2>{{ 'members.data' | transloco }}</h2>
          <dl class="kv">
            <dt>{{ 'members.email' | transloco }}</dt>
            <dd>{{ m.email ?? '—' }}</dd>
            <dt>{{ 'members.phone' | transloco }}</dt>
            <dd>{{ m.phone ?? '—' }}</dd>
            <dt>{{ 'members.birthDate' | transloco }}</dt>
            <dd>{{ m.birthDate | fcDate }}</dd>
            <dt>{{ 'members.address' | transloco }}</dt>
            <dd>{{ m.address ?? '—' }}</dd>
            <dt>{{ 'members.enrolledOn' | transloco }}</dt>
            <dd>{{ m.enrolledOn | fcDate }}</dd>
            <dt>{{ 'members.emergency' | transloco }}</dt>
            <dd>
              {{ m.emergencyContactName ?? '—' }}
              @if (m.emergencyContactPhone) {
                · {{ m.emergencyContactPhone }}
              }
              @if (m.emergencyContactRelationship) {
                ({{ m.emergencyContactRelationship }})
              }
            </dd>
            <dt>{{ 'members.notes' | transloco }}</dt>
            <dd>{{ m.notes ?? '—' }}</dd>
          </dl>
        </section>

        <section class="card section">
          <h2>{{ 'members.qr' | transloco }}</h2>
          <p class="muted">
            {{ (m.hasQrToken ? 'members.qrHas' : 'members.qrNone') | transloco: { date: (m.qrTokenIssuedAt | fcDateTime) } }}
          </p>
          @if (can(perm.MembersUpdate)) {
            <button type="button" class="btn" (click)="dialog.set('qrConfirm')">
              {{ (m.hasQrToken ? 'members.qrReplace' : 'members.qrGenerate') | transloco }}
            </button>
          }
        </section>
      </div>

      <section class="card section">
        <h2>{{ 'members.whatsapp.title' | transloco }}</h2>
        <p class="muted">
          @if (m.whatsAppConsent) {
            {{ 'members.whatsapp.granted' | transloco: { date: (m.whatsAppConsentAt | fcDateTime) } }}
          } @else {
            {{ 'members.whatsapp.notGranted' | transloco }}
          }
        </p>
        @if (!m.phone && !m.whatsAppConsent) {
          <p class="muted">{{ 'members.whatsapp.noPhone' | transloco }}</p>
        }
        @if (can(perm.MembersUpdate) && (m.whatsAppConsent || m.phone)) {
          <button type="button" class="btn" (click)="dialog.set('whatsapp')">
            {{ (m.whatsAppConsent ? 'members.whatsapp.revoke' : 'members.whatsapp.grant') | transloco }}
          </button>
        }
      </section>

      <section class="section">
        <h2>{{ 'members.memberships' | transloco }}</h2>
        <div class="card table-card">
          <table class="table">
            <thead>
              <tr>
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
              @for (ms of memberships(); track ms.id) {
                <tr>
                  <td>{{ ms.planName }}</td>
                  <td>{{ ms.startDate | fcDate }}</td>
                  <td>{{ ms.endDate | fcDate }}</td>
                  <td class="num">{{ ms.agreedPrice | fcMoney }}</td>
                  <td class="num">{{ ms.obligation?.balance | fcMoney }}</td>
                  <td><fc-badge [tone]="msTone(ms.status)">{{ 'enums.membershipStatus.' + ms.status | transloco }}</fc-badge></td>
                  <td class="actions"><a class="btn" [routerLink]="['/memberships', ms.id]">{{ 'common.view' | transloco }}</a></td>
                </tr>
              } @empty {
                <tr><td colspan="7" class="empty">{{ 'members.noMemberships' | transloco }}</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>

      <section class="section">
        <h2>{{ 'members.obligations' | transloco }}</h2>
        <div class="card table-card">
          <table class="table">
            <thead>
              <tr>
                <th>{{ 'billing.reference' | transloco }}</th>
                <th>{{ 'billing.concept' | transloco }}</th>
                <th>{{ 'billing.dueDate' | transloco }}</th>
                <th class="num">{{ 'billing.payable' | transloco }}</th>
                <th class="num">{{ 'billing.paid' | transloco }}</th>
                <th class="num">{{ 'billing.balance' | transloco }}</th>
                <th>{{ 'common.status' | transloco }}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (o of obligations(); track o.id) {
                <tr>
                  <td>{{ o.reference }}</td>
                  <td>{{ o.concept }}</td>
                  <td>{{ o.dueDate | fcDate }}</td>
                  <td class="num">{{ o.payableAmount | fcMoney }}</td>
                  <td class="num">{{ o.paidAmount | fcMoney }}</td>
                  <td class="num">{{ o.balance | fcMoney }}</td>
                  <td><fc-badge [tone]="obTone(o.status)">{{ 'enums.obligationStatus.' + o.status | transloco }}</fc-badge></td>
                  <td class="actions">
                    @if (can(perm.PaymentsCreate) && o.balance > 0 && o.status !== 'Voided') {
                      <button type="button" class="btn" (click)="openPayment(o.id)">{{ 'billing.payments.pay' | transloco }}</button>
                    }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="8" class="empty">{{ 'members.noObligations' | transloco }}</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>

      <section class="section">
        <h2>{{ 'members.payments' | transloco }}</h2>
        <div class="card table-card">
          <table class="table">
            <thead>
              <tr>
                <th>{{ 'billing.payments.receipt' | transloco }}</th>
                <th>{{ 'billing.payments.date' | transloco }}</th>
                <th>{{ 'billing.payments.method' | transloco }}</th>
                <th class="num">{{ 'billing.payments.amount' | transloco }}</th>
                <th>{{ 'billing.payments.status' | transloco }}</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              @for (p of payments(); track p.id) {
                <tr>
                  <td>{{ p.receiptNumber }}</td>
                  <td>{{ p.paidAt | fcDateTime }}</td>
                  <td>{{ 'enums.paymentMethod.' + p.method | transloco }}</td>
                  <td class="num">{{ p.amount | fcMoney }}</td>
                  <td><fc-badge [tone]="payTone(p.status)">{{ 'enums.paymentStatus.' + p.status | transloco }}</fc-badge></td>
                  <td class="actions"><button type="button" class="btn" (click)="receiptId.set(p.id)">{{ 'billing.payments.viewReceipt' | transloco }}</button></td>
                </tr>
              } @empty {
                <tr><td colspan="6" class="empty">{{ 'members.noPayments' | transloco }}</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>

      @if (can(perm.AttendanceRead)) {
        <section class="section">
          <h2>{{ 'members.attendance' | transloco }}</h2>
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'attendance.in' | transloco }}</th>
                  <th>{{ 'attendance.out' | transloco }}</th>
                  <th>{{ 'common.branch' | transloco }}</th>
                  <th>{{ 'attendance.method' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (a of visits(); track a.id) {
                  <tr>
                    <td>{{ a.checkInAt | fcDateTime }}</td>
                    <td>{{ a.checkOutAt | fcDateTime }}</td>
                    <td>{{ a.branchName }}</td>
                    <td>
                      {{ 'enums.attendanceMethod.' + a.method | transloco }}
                      @if (a.isException) {
                        <fc-badge tone="warn">{{ 'attendance.exception' | transloco }}</fc-badge>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="4" class="empty">{{ 'members.noAttendance' | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }

      @switch (dialog()) {
        @case ('edit') {
          <fc-member-form [member]="m" (closed)="dialog.set(null)" (saved)="onChanged($event)" />
        }
        @case ('enroll') {
          <fc-enroll-dialog [member]="m" (closed)="dialog.set(null)" (done)="onEnrolled()" />
        }
        @case ('payment') {
          <fc-payment-dialog [member]="m" [obligationId]="payObligation()" (closed)="dialog.set(null)" (done)="onPaid($event)" />
        }
        @case ('status') {
          <fc-confirm
            [title]="(m.status === 'Active' ? 'members.deactivate' : 'members.activate') | transloco"
            [message]="(m.status === 'Active' ? 'members.deactivateConfirm' : 'members.activateConfirm') | transloco: { name: m.firstName + ' ' + m.lastName }"
            [confirmLabel]="(m.status === 'Active' ? 'members.deactivate' : 'members.activate') | transloco"
            [danger]="m.status === 'Active'"
            (confirmed)="toggleStatus(m)"
            (cancelled)="dialog.set(null)"
          />
        }
        @case ('whatsapp') {
          <fc-confirm
            [title]="'members.whatsapp.title' | transloco"
            [message]="(m.whatsAppConsent ? 'members.whatsapp.revokeConfirm' : 'members.whatsapp.grantConfirm') | transloco: { name: m.firstName }"
            [confirmLabel]="(m.whatsAppConsent ? 'members.whatsapp.revoke' : 'members.whatsapp.grant') | transloco"
            [danger]="m.whatsAppConsent"
            (confirmed)="toggleConsent(m)"
            (cancelled)="dialog.set(null)"
          />
        }
        @case ('qrConfirm') {
          <fc-confirm
            [title]="'members.qr' | transloco"
            [message]="(m.hasQrToken ? 'members.qrReplaceConfirm' : 'members.qrWarning') | transloco"
            [confirmLabel]="(m.hasQrToken ? 'members.qrReplace' : 'members.qrGenerate') | transloco"
            (confirmed)="issueQr(m)"
            (cancelled)="dialog.set(null)"
          />
        }
        @case ('qr') {
          <fc-modal [title]="'members.qr' | transloco" (closed)="closeQr()">
            @if (qrError()) {
              <p class="alert alert-danger" role="alert">{{ qrError() }}</p>
            }
            <div class="qr-box">
              @if (qrImage()) {
                <img [src]="qrImage()" [alt]="'members.qr' | transloco" />
              }
              <p class="alert alert-warning">{{ 'members.qrWarning' | transloco }}</p>
              <p>{{ 'members.qrToken' | transloco }}: <code>{{ qrToken() }}</code></p>
            </div>
            <ng-container footer>
              <button type="button" class="btn" (click)="print()">{{ 'common.print' | transloco }}</button>
              <button type="button" class="btn btn-primary" (click)="closeQr()">{{ 'common.close' | transloco }}</button>
            </ng-container>
          </fc-modal>
        }
      }

      @if (receiptId(); as id) {
        <fc-receipt-view [paymentId]="id" (closed)="receiptId.set(null)" />
      }
    } @else {
      <p class="muted">{{ 'common.loading' | transloco }}</p>
    }
  `,
})
export class MemberDetail implements OnInit {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly perm = Perm;
  protected readonly member = signal<Member | null>(null);
  protected readonly memberships = signal<Membership[]>([]);
  protected readonly obligations = signal<Obligation[]>([]);
  protected readonly payments = signal<Payment[]>([]);
  protected readonly visits = signal<Attendance[]>([]);
  protected readonly error = signal<string | null>(null);

  protected readonly dialog = signal<'edit' | 'enroll' | 'payment' | 'status' | 'whatsapp' | 'qrConfirm' | 'qr' | null>(null);
  protected readonly payObligation = signal<string | null>(null);
  protected readonly receiptId = signal<string | null>(null);
  protected readonly qrToken = signal('');
  protected readonly qrImage = signal('');
  protected readonly qrError = signal<string | null>(null);

  protected readonly msTone = membershipTone;
  protected readonly obTone = obligationTone;
  protected readonly payTone = paymentTone;
  private readonly hasAttendance = computed(() => this.session.has(Perm.AttendanceRead));

  ngOnInit(): void {
    this.catalog.ensure();
    this.route.paramMap
      .pipe(
        switchMap((params) => this.api.member(params.get('id') ?? '')),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe({
        next: (member) => {
          this.member.set(member);
          this.loadRelated(member.id);
        },
        error: (err: unknown) => this.error.set(this.errors.message(err)),
      });
  }

  protected can(permission: string): boolean {
    return this.session.has(permission);
  }

  protected openPayment(obligationId: string | null): void {
    this.payObligation.set(obligationId);
    this.dialog.set('payment');
  }

  protected onChanged(member: Member): void {
    this.member.set(member);
    this.dialog.set(null);
  }

  protected onEnrolled(): void {
    this.dialog.set(null);
    this.reload();
  }

  protected onPaid(payment: Payment): void {
    this.dialog.set(null);
    this.reload();
    this.receiptId.set(payment.id);
  }

  protected toggleStatus(member: Member): void {
    this.api.setMemberStatus(member.id, member.status !== 'Active').subscribe({
      next: (updated) => {
        this.member.set(updated);
        this.dialog.set(null);
        this.toast.success('members.savedToast');
      },
      error: (err: unknown) => {
        this.dialog.set(null);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected toggleConsent(member: Member): void {
    this.api.setWhatsAppConsent(member.id, !member.whatsAppConsent).subscribe({
      next: (updated) => {
        this.member.set(updated);
        this.dialog.set(null);
        this.toast.success(updated.whatsAppConsent ? 'members.whatsapp.grantedToast' : 'members.whatsapp.revokedToast');
      },
      error: (err: unknown) => {
        this.dialog.set(null);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected issueQr(member: Member): void {
    this.qrError.set(null);
    this.api.issueMemberQr(member.id).subscribe({
      next: async (qr) => {
        this.qrToken.set(qr.token);
        try {
          const module = await import('qrcode');
          const lib = (module as unknown as { default?: typeof module }).default ?? module;
          this.qrImage.set(await lib.toDataURL(qr.token, { width: 224, margin: 1, errorCorrectionLevel: 'M' }));
        } catch {
          this.qrImage.set('');
        }
        this.dialog.set('qr');
        this.reload();
      },
      error: (err: unknown) => {
        this.dialog.set(null);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected closeQr(): void {
    // El token no se conserva en pantalla después de cerrar: el servidor solo guarda su huella.
    this.qrToken.set('');
    this.qrImage.set('');
    this.dialog.set(null);
  }

  protected print(): void {
    window.print();
  }

  private reload(): void {
    const member = this.member();
    if (member) {
      this.api.member(member.id).subscribe((updated) => this.member.set(updated));
      this.loadRelated(member.id);
    }
  }

  private loadRelated(id: string): void {
    this.api.memberships({ memberId: id, pageSize: 50 }).subscribe((page) => this.memberships.set(page.items));
    this.api.obligations({ memberId: id, pageSize: 50 }).subscribe((page) => this.obligations.set(page.items));
    this.api.payments({ memberId: id, pageSize: 20 }).subscribe((page) => this.payments.set(page.items));
    if (this.hasAttendance()) {
      this.api.attendance({ memberId: id, pageSize: 10 }).subscribe((page) => this.visits.set(page.items));
    }
  }
}
