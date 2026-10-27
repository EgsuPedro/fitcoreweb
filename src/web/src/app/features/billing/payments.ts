import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Payment } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, paymentTone } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { Confirm } from '../../shared/confirm';
import { FcDateTime, FcMoney } from '../../shared/format.pipes';
import { AttemptKey } from '../../shared/idempotency';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';
import { PaymentDialog } from './payment-dialog';
import { ReceiptView } from './receipt-view';

type Action = { kind: 'confirm' | 'reverse'; payment: Payment };

/** Cobros: listado de pagos, registro de uno nuevo, confirmación de transferencias, reversos y recibo. */
@Component({
  selector: 'fc-payments',
  imports: [RouterLink, TranslocoPipe, Badge, Pager, Confirm, ReasonDialog, FcDateTime, FcMoney, PaymentDialog, ReceiptView],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'billing.payments.title' | transloco }}</h1>
      @if (canCreate()) {
        <button type="button" class="btn btn-primary" (click)="registering.set(true)">{{ 'billing.payments.new' | transloco }}</button>
      }
    </header>

    <div class="toolbar">
      <input type="search" class="search" [placeholder]="'common.search' | transloco" [attr.aria-label]="'common.search' | transloco" (input)="list.search($any($event.target).value)" />
      <select [attr.aria-label]="'billing.payments.status' | transloco" (change)="list.set({ status: $any($event.target).value })">
        <option value="">{{ 'common.all' | transloco }}</option>
        @for (status of statuses; track status) {
          <option [value]="status">{{ 'enums.paymentStatus.' + status | transloco }}</option>
        }
      </select>
      <select [attr.aria-label]="'billing.payments.method' | transloco" (change)="list.set({ method: $any($event.target).value })">
        <option value="">{{ 'common.all' | transloco }}</option>
        @for (method of methods; track method) {
          <option [value]="method">{{ 'enums.paymentMethod.' + method | transloco }}</option>
        }
      </select>
      <select [attr.aria-label]="'common.branch' | transloco" (change)="list.set({ branchId: $any($event.target).value })">
        <option value="">{{ 'common.allBranches' | transloco }}</option>
        @for (branch of catalog.branches(); track branch.id) {
          <option [value]="branch.id">{{ branch.name }}</option>
        }
      </select>
      <label class="inline-field">
        <span>{{ 'common.from' | transloco }}</span>
        <input type="date" (change)="list.set({ from: $any($event.target).value })" />
      </label>
      <label class="inline-field">
        <span>{{ 'common.to' | transloco }}</span>
        <input type="date" (change)="list.set({ to: $any($event.target).value })" />
      </label>
    </div>

    @if (list.error()) {
      <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
    }
    @if (actionError() && action()?.kind !== 'reverse') {
      <p class="alert alert-danger" role="alert">{{ actionError() }}</p>
    }

    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'billing.payments.receipt' | transloco }}</th>
            <th>{{ 'billing.payments.date' | transloco }}</th>
            <th>{{ 'billing.payments.member' | transloco }}</th>
            <th>{{ 'billing.payments.method' | transloco }}</th>
            <th class="num">{{ 'billing.payments.amount' | transloco }}</th>
            <th>{{ 'billing.payments.status' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (p of list.items(); track p.id) {
            <tr>
              <td><strong>{{ p.receiptNumber }}</strong></td>
              <td>{{ p.paidAt | fcDateTime }}</td>
              <td><a [routerLink]="['/members', p.memberId]">{{ p.memberName }}</a></td>
              <td>{{ 'enums.paymentMethod.' + p.method | transloco }}@if (p.reference) { <small class="muted"> · {{ p.reference }}</small> }</td>
              <td class="num">{{ p.amount | fcMoney }}</td>
              <td>
                <fc-badge [tone]="tone(p.status)">{{ 'enums.paymentStatus.' + p.status | transloco }}</fc-badge>
                @if (p.reversalReason) {
                  <br /><small class="muted">{{ 'billing.payments.reversedBy' | transloco: { reason: p.reversalReason } }}</small>
                }
              </td>
              <td class="actions">
                <button type="button" class="btn" (click)="receiptId.set(p.id)">{{ 'billing.payments.viewReceipt' | transloco }}</button>
                @if (p.status === 'PendingVerification' && canConfirm()) {
                  <button type="button" class="btn btn-primary" (click)="start('confirm', p)">{{ 'billing.payments.confirm' | transloco }}</button>
                }
                @if ((p.status === 'Confirmed' || p.status === 'PendingVerification') && canReverse()) {
                  <button type="button" class="btn btn-danger" (click)="start('reverse', p)">{{ 'billing.payments.reverse' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="7" class="empty">{{ (list.loading() ? 'common.loading' : 'billing.payments.empty') | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    @if (registering()) {
      <fc-payment-dialog (closed)="registering.set(false)" (done)="onRegistered($event)" />
    }
    @if (receiptId(); as id) {
      <fc-receipt-view [paymentId]="id" (closed)="receiptId.set(null)" />
    }
    @if (action(); as a) {
      @if (a.kind === 'confirm') {
        <fc-confirm
          [title]="'billing.payments.confirm' | transloco"
          [message]="'billing.payments.confirmQuestionTransfer' | transloco"
          [confirmLabel]="'billing.payments.confirm' | transloco"
          (confirmed)="confirm(a.payment)"
          (cancelled)="action.set(null)"
        />
      } @else {
        <fc-reason-dialog
          [title]="'billing.payments.reverse' | transloco"
          [label]="'billing.payments.reverseReason' | transloco"
          [hint]="'billing.payments.reverseHint' | transloco"
          [confirmLabel]="'billing.payments.reverse' | transloco"
          [danger]="true"
          [busy]="busy()"
          [error]="actionError()"
          (submitted)="reverse(a.payment, $event)"
          (cancelled)="action.set(null)"
        />
      }
    }
  `,
})
export class Payments implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly list = new ListState<Payment>((query) => this.api.payments(query), this.errors, inject(DestroyRef));
  protected readonly statuses = ['PendingVerification', 'Confirmed', 'Reversed'];
  protected readonly methods = ['Cash', 'BankTransfer', 'Card', 'Other'];
  protected readonly tone = paymentTone;
  protected readonly canCreate = computed(() => this.session.has(Perm.PaymentsCreate));
  protected readonly canConfirm = computed(() => this.session.has(Perm.PaymentsConfirm));
  protected readonly canReverse = computed(() => this.session.has(Perm.PaymentsReverse));
  protected readonly registering = signal(false);
  protected readonly receiptId = signal<string | null>(null);
  protected readonly action = signal<Action | null>(null);
  protected readonly actionError = signal<string | null>(null);
  protected readonly busy = signal(false);

  private readonly attempt = new AttemptKey();

  ngOnInit(): void {
    this.catalog.ensure();
    this.list.load();
  }

  protected start(kind: Action['kind'], payment: Payment): void {
    this.actionError.set(null);
    this.attempt.renew();
    this.action.set({ kind, payment });
  }

  protected onRegistered(payment: Payment): void {
    this.registering.set(false);
    this.list.load();
    this.receiptId.set(payment.id);
  }

  protected confirm(payment: Payment): void {
    this.perform(this.api.confirmPayment(payment.id, this.attempt.value), 'billing.payments.confirmed');
  }

  protected reverse(payment: Payment, reason: string): void {
    this.perform(this.api.reversePayment(payment.id, reason, this.attempt.value), 'billing.payments.reversed');
  }

  /** El aviso de éxito solo aparece cuando el servidor respondió; los errores se muestran sin cerrar el diálogo. */
  private perform(request: ReturnType<ApiService['confirmPayment']>, toastKey: string): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.actionError.set(null);
    request.subscribe({
      next: () => {
        this.busy.set(false);
        this.attempt.renew();
        this.action.set(null);
        this.toast.success(toastKey);
        this.list.load();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.attempt.afterError(err);
        this.actionError.set(this.errors.message(err));
        // La confirmación no tiene dónde mostrar el error dentro del diálogo: se cierra y el aviso queda en la página.
        if (this.action()?.kind === 'confirm') {
          this.action.set(null);
        }
      },
    });
  }
}
