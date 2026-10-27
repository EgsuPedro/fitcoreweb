import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Member, Obligation, Payment } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, obligationTone } from '../../shared/badge';
import { looksLikeCardNumber } from '../../shared/card-number';
import { FcDate, FcMoney } from '../../shared/format.pipes';
import { AttemptKey } from '../../shared/idempotency';
import { MemberPicker } from '../../shared/member-picker';
import { Modal } from '../../shared/modal';
import { ToastService } from '../../shared/toast';

type Method = 'Cash' | 'BankTransfer' | 'Card' | 'Other';
const METHODS: readonly Method[] = ['Cash', 'BankTransfer', 'Card', 'Other'];

/**
 * Registro de un pago o abono. El saldo que se muestra es solo una ayuda: el backend recalcula y valida todo (importe,
 * asignaciones, caja) y es quien rechaza un exceso. La clave de idempotencia evita duplicar el pago ante un reintento.
 */
@Component({
  selector: 'fc-payment-dialog',
  imports: [ReactiveFormsModule, TranslocoPipe, Modal, MemberPicker, FcMoney, FcDate, Badge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="'billing.payments.new' | transloco" (closed)="closed.emit()">
      @if (formError()) {
        <p class="alert alert-danger" role="alert">{{ formError() }}</p>
      }

      @if (!reviewing()) {
        <form id="payment-form" [formGroup]="form" (ngSubmit)="review()" novalidate>
          <div class="field">
            <span>{{ 'billing.payments.pickMember' | transloco }}</span>
            <fc-member-picker [initial]="member()" [locked]="!!member()" (picked)="onMember($event)" />
          </div>

          @if (selected()) {
            <div class="section">
              <h3>{{ 'billing.payments.selectObligations' | transloco }}</h3>
              @if (loadingObligations()) {
                <p class="muted">{{ 'common.loading' | transloco }}</p>
              } @else if (open().length === 0) {
                <p class="muted">{{ 'billing.payments.noOpen' | transloco }}</p>
              } @else {
                <table class="table">
                  <thead>
                    <tr>
                      <th></th>
                      <th>{{ 'billing.concept' | transloco }}</th>
                      <th>{{ 'billing.dueDate' | transloco }}</th>
                      <th class="num">{{ 'billing.balance' | transloco }}</th>
                      <th class="num">{{ 'billing.payments.amount' | transloco }}</th>
                    </tr>
                  </thead>
                  <tbody>
                    @for (o of open(); track o.id) {
                      <tr>
                        <td>
                          <input type="checkbox" [checked]="chosen().has(o.id)" [attr.aria-label]="o.reference" (change)="toggle(o, $any($event.target).checked)" />
                        </td>
                        <td>
                          {{ o.reference }} · {{ o.concept }}
                          <fc-badge [tone]="tone(o.status)">{{ 'enums.obligationStatus.' + o.status | transloco }}</fc-badge>
                        </td>
                        <td>{{ o.dueDate | fcDate }}</td>
                        <td class="num">{{ o.balance | fcMoney }}</td>
                        <td class="num">
                          @if (chosen().has(o.id)) {
                            <input type="number" min="0.01" step="0.01" class="amount" [value]="amounts()[o.id]" [attr.aria-label]="'billing.payments.amount' | transloco" (input)="setAmount(o.id, $any($event.target).value)" />
                          }
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
                <p class="muted">{{ 'billing.payments.autoAllocate' | transloco }}</p>
              }
            </div>

            <div class="form-grid">
              <label class="field">
                <span>{{ 'billing.payments.method' | transloco }}</span>
                <select formControlName="method">
                  @for (method of methods; track method) {
                    <option [value]="method">{{ 'enums.paymentMethod.' + method | transloco }}</option>
                  }
                </select>
              </label>
              <label class="field">
                <span>{{ 'billing.payments.amount' | transloco }}</span>
                @if (chosen().size > 0) {
                  <input type="number" readonly [value]="allocatedTotal()" />
                } @else {
                  <input type="number" min="0.01" step="0.01" formControlName="amount" />
                }
              </label>
            </div>
            @if (method() === 'Cash') {
              <p class="muted">{{ 'billing.payments.cashHint' | transloco }}</p>
            }
            @if (method() === 'BankTransfer') {
              <p class="muted">{{ 'billing.payments.transferHint' | transloco }}</p>
            }
            @if (method() !== 'Cash') {
              <label class="field">
                <span>{{ 'billing.payments.referenceLabel' | transloco }}</span>
                <input formControlName="reference" maxlength="100" autocomplete="off" />
                <small class="muted">{{ 'billing.payments.referenceHint' | transloco }}</small>
                @if (form.controls.reference.touched && referenceError()) {
                  <small class="field-error" role="alert">{{ referenceError() }}</small>
                }
              </label>
            }
            @if (method() === 'BankTransfer') {
              <label class="field">
                <span>{{ 'billing.payments.bank' | transloco }}</span>
                <input formControlName="bankName" maxlength="100" />
              </label>
            }
            @if (method() === 'Other') {
              <label class="field">
                <span>{{ 'billing.payments.methodDetail' | transloco }}</span>
                <input formControlName="methodDetail" maxlength="100" />
              </label>
            }
            <label class="field">
              <span>{{ 'billing.payments.notes' | transloco }}</span>
              <textarea formControlName="notes" maxlength="500"></textarea>
            </label>
            @if (remaining() !== null) {
              <p [class.alert]="remaining()! < 0" [class.alert-danger]="remaining()! < 0" [class.muted]="remaining()! >= 0">
                @if (remaining()! < 0) {
                  {{ 'billing.payments.overpay' | transloco }}
                } @else {
                  {{ 'billing.payments.remaining' | transloco: { amount: formatMoney(remaining()!) } }}
                }
              </p>
            }
          }
        </form>
      } @else {
        <h3>{{ 'billing.payments.summary' | transloco }}</h3>
        <dl class="kv">
          <dt>{{ 'billing.payments.member' | transloco }}</dt>
          <dd>{{ selected()?.firstName }} {{ selected()?.lastName }}</dd>
          <dt>{{ 'billing.payments.method' | transloco }}</dt>
          <dd>{{ 'enums.paymentMethod.' + method() | transloco }}</dd>
          <dt>{{ 'billing.payments.amount' | transloco }}</dt>
          <dd><strong>{{ total() | fcMoney }}</strong></dd>
          @if (form.controls.reference.value) {
            <dt>{{ 'billing.reference' | transloco }}</dt>
            <dd>{{ form.controls.reference.value }}</dd>
          }
        </dl>
        <p>{{ 'billing.payments.confirmQuestion' | transloco }}</p>
      }

      <ng-container footer>
        @if (reviewing()) {
          <button type="button" class="btn" [disabled]="saving()" (click)="reviewing.set(false)">{{ 'common.back' | transloco }}</button>
          <button type="button" class="btn btn-primary" [disabled]="saving()" (click)="submit()">
            {{ (saving() ? 'common.processing' : 'billing.payments.pay') | transloco }}
          </button>
        } @else {
          <button type="button" class="btn" (click)="closed.emit()">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="payment-form" class="btn btn-primary" [disabled]="!selected()">{{ 'common.review' | transloco }}</button>
        }
      </ng-container>
    </fc-modal>
  `,
})
export class PaymentDialog implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly transloco = inject(TranslocoService);
  private readonly fb = inject(FormBuilder);

  readonly member = input<Member | null>(null);
  /** Obligación que se marca de entrada (al cobrar desde la ficha de una membresía). */
  readonly obligationId = input<string | null>(null);
  readonly done = output<Payment>();
  readonly closed = output<void>();

  protected readonly methods = METHODS;
  protected readonly selected = signal<Member | null>(null);
  protected readonly open = signal<Obligation[]>([]);
  protected readonly loadingObligations = signal(false);
  protected readonly chosen = signal<Set<string>>(new Set());
  protected readonly amounts = signal<Record<string, string>>({});
  protected readonly reviewing = signal(false);
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);

  private readonly attempt = new AttemptKey();

  protected readonly form = this.fb.nonNullable.group({
    method: ['Card' as Method],
    amount: [null as number | null],
    reference: [''],
    bankName: [''],
    methodDetail: [''],
    notes: [''],
  });

  private readonly formValue = signal(this.form.getRawValue());
  protected readonly method = computed(() => this.formValue().method);
  protected readonly allocatedTotal = computed(() =>
    this.round([...this.chosen()].reduce((sum, id) => sum + (Number(this.amounts()[id]) || 0), 0)),
  );
  protected readonly total = computed(() => (this.chosen().size > 0 ? this.allocatedTotal() : Number(this.formValue().amount) || 0));
  protected readonly remaining = computed(() => {
    const balance = this.open().reduce((sum, o) => sum + o.balance, 0);
    return this.total() > 0 && this.open().length > 0 ? this.round(balance - this.total()) : null;
  });

  constructor() {
    this.form.valueChanges.subscribe(() => {
      this.formValue.set(this.form.getRawValue());
      this.attempt.renew();
    });
  }

  ngOnInit(): void {
    if (this.member()) {
      this.onMember(this.member());
    }
  }

  protected tone = obligationTone;

  protected formatMoney(value: number): string {
    return new Intl.NumberFormat(this.transloco.getActiveLang() === 'en' ? 'en-US' : 'es-EC', { style: 'currency', currency: 'USD' }).format(value);
  }

  protected onMember(member: Member | null): void {
    this.selected.set(member);
    this.open.set([]);
    this.chosen.set(new Set());
    if (!member) {
      return;
    }
    this.loadingObligations.set(true);
    this.api.obligations({ memberId: member.id, pageSize: 100 }).subscribe({
      next: (page) => {
        const open = page.items.filter((o) => o.balance > 0 && o.status !== 'Voided' && o.status !== 'Paid');
        this.open.set(open.sort((a, b) => a.dueDate.localeCompare(b.dueDate)));
        this.loadingObligations.set(false);
        const preselect = open.find((o) => o.id === this.obligationId());
        if (preselect) {
          this.toggle(preselect, true);
        }
      },
      error: (err: unknown) => {
        this.loadingObligations.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  protected toggle(obligation: Obligation, checked: boolean): void {
    const next = new Set(this.chosen());
    if (checked) {
      next.add(obligation.id);
      this.amounts.update((a) => ({ ...a, [obligation.id]: String(obligation.balance) }));
    } else {
      next.delete(obligation.id);
    }
    this.chosen.set(next);
    this.attempt.renew();
  }

  protected setAmount(id: string, value: string): void {
    this.amounts.update((a) => ({ ...a, [id]: value }));
    this.attempt.renew();
  }

  protected referenceError(): string | null {
    const { method, reference } = this.formValue();
    if (method === 'Cash') {
      return null;
    }
    if ((method === 'BankTransfer' || method === 'Card') && !reference.trim()) {
      return this.transloco.translate('common.required');
    }
    return looksLikeCardNumber(reference) ? this.transloco.translate('billing.payments.referenceHint') : null;
  }

  protected review(): void {
    this.form.markAllAsTouched();
    const value = this.form.getRawValue();
    const invalid =
      !this.selected() ||
      this.total() <= 0 ||
      !!this.referenceError() ||
      (value.method === 'Other' && !value.methodDetail.trim());
    if (invalid) {
      this.formError.set(this.transloco.translate('billing.payments.invalidForm'));
      return;
    }
    this.formError.set(null);
    this.reviewing.set(true);
  }

  protected submit(): void {
    const member = this.selected();
    if (!member) {
      return;
    }
    const v = this.form.getRawValue();
    const allocations = [...this.chosen()].map((id) => ({ obligationId: id, amount: this.round(Number(this.amounts()[id])) }));

    this.saving.set(true);
    this.formError.set(null);
    this.api
      .createPayment(
        {
          memberId: member.id,
          method: v.method,
          amount: this.total(),
          reference: v.reference.trim() || null,
          bankName: v.bankName.trim() || null,
          methodDetail: v.methodDetail.trim() || null,
          paidAt: null,
          notes: v.notes.trim() || null,
          allocations: allocations.length > 0 ? allocations : null,
        },
        this.attempt.value,
      )
      .subscribe({
        next: (payment) => {
          this.saving.set(false);
          this.attempt.renew();
          this.toast.success('billing.payments.registered', { receipt: payment.receiptNumber });
          this.done.emit(payment);
        },
        error: (err: unknown) => {
          this.saving.set(false);
          this.attempt.afterError(err);
          this.reviewing.set(false);
          this.formError.set(this.errors.message(err));
        },
      });
  }

  private round(value: number): number {
    return Math.round(value * 100) / 100;
  }
}
