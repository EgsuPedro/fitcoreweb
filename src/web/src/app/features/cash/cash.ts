import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { Observable } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { CashMovement, CashRegister, CashSession } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages, Formatter } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { BranchCatalog } from '../../shared/branch-catalog';
import { FcDateTime, FcMoney } from '../../shared/format.pipes';
import { AttemptKey } from '../../shared/idempotency';
import { ListState } from '../../shared/list-state';
import { Modal } from '../../shared/modal';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

type Dialog = 'open' | 'close' | 'movement' | 'register' | null;

/** Caja: cajas por sucursal, apertura y cierre con arqueo, movimientos manuales e historial de cierres. */
@Component({
  selector: 'fc-cash',
  imports: [ReactiveFormsModule, TranslocoPipe, Badge, Modal, Pager, FcDateTime, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'cash.title' | transloco }}</h1>
      @if (canManage()) {
        <button type="button" class="btn btn-primary" (click)="openRegisterForm('new')">{{ 'cash.newRegister' | transloco }}</button>
      }
    </header>

    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    <h2>{{ 'cash.registers' | transloco }}</h2>
    <div class="card table-card">
      <table class="table">
        <thead>
          <tr>
            <th>{{ 'cash.name' | transloco }}</th>
            <th>{{ 'common.branch' | transloco }}</th>
            <th>{{ 'cash.state' | transloco }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          @for (r of registers(); track r.id) {
            <tr [class.selected]="selected()?.id === r.id">
              <td><strong>{{ r.name }}</strong> @if (!r.isActive) { <fc-badge tone="off">{{ 'common.inactive' | transloco }}</fc-badge> }</td>
              <td>{{ r.branchName }}</td>
              <td>
                @if (r.openSessions.length === 0) {
                  <fc-badge tone="neutral">{{ 'cash.free' | transloco }}</fc-badge>
                } @else {
                  @for (s of r.openSessions; track s.id) {
                    <fc-badge tone="ok">{{ 'cash.openBy' | transloco: { name: s.openedByName ?? '—' } }}</fc-badge>
                  }
                }
              </td>
              <td class="actions">
                <button type="button" class="btn" (click)="select(r)">{{ 'cash.select' | transloco }}</button>
                @if (canManage()) {
                  <button type="button" class="btn" (click)="openRegisterForm(r)">{{ 'common.edit' | transloco }}</button>
                }
              </td>
            </tr>
          } @empty {
            <tr><td colspan="4" class="empty">{{ 'cash.empty' | transloco }}</td></tr>
          }
        </tbody>
      </table>
    </div>

    @if (selected(); as reg) {
      <section class="section">
        <h2>{{ reg.name }} · {{ reg.branchName }}</h2>

        @if (activeSession(); as s) {
          <div class="card section">
            <h3>{{ 'cash.session' | transloco }}</h3>
            <div class="stats">
              <div class="stat"><span class="muted">{{ 'cash.opening' | transloco }}</span><strong>{{ s.openingAmount | fcMoney }}</strong></div>
              <div class="stat"><span class="muted">{{ 'cash.totalIn' | transloco }}</span><strong>{{ s.totalIn | fcMoney }}</strong></div>
              <div class="stat"><span class="muted">{{ 'cash.totalOut' | transloco }}</span><strong>{{ s.totalOut | fcMoney }}</strong></div>
              <div class="stat"><span class="muted">{{ 'cash.expected' | transloco }}</span><strong>{{ s.expectedAmount | fcMoney }}</strong></div>
            </div>
            <p class="muted">{{ 'cash.openedAt' | transloco }}: {{ s.openedAt | fcDateTime }} · {{ s.openedByName }}</p>
            @if (canOperate()) {
              <div class="inline-actions">
                <button type="button" class="btn" (click)="startMovement()">{{ 'cash.newMovement' | transloco }}</button>
                <button type="button" class="btn btn-primary" (click)="startClose(s)">{{ 'cash.close' | transloco }}</button>
              </div>
            }
          </div>

          <h3>{{ 'cash.movementsOf' | transloco }}</h3>
          <div class="card table-card">
            <table class="table">
              <thead>
                <tr>
                  <th>{{ 'common.date' | transloco }}</th>
                  <th>{{ 'cash.movementType' | transloco }}</th>
                  <th>{{ 'cash.movementReason' | transloco }}</th>
                  <th>{{ 'cash.receipt' | transloco }}</th>
                  <th class="num">{{ 'common.amount' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (m of movements.items(); track m.id) {
                  <tr>
                    <td>{{ m.createdAt | fcDateTime }}</td>
                    <td>{{ 'enums.cashMovementType.' + m.type | transloco }}</td>
                    <td>{{ m.reason ?? '—' }}</td>
                    <td>{{ m.receiptNumber ?? '—' }}</td>
                    <td class="num">{{ m.direction === 'Out' ? '−' : '+' }}{{ m.amount | fcMoney }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="5" class="empty">{{ 'cash.noMovements' | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
          <fc-pager [page]="movements.page()" [pageSize]="movements.pageSize" [total]="movements.total()" (pageChange)="movements.goTo($event)" />
        } @else {
          <p class="muted">{{ 'cash.noSession' | transloco }}</p>
          @if (canOperate() && reg.isActive && (reg.allowMultipleOpenSessions || reg.openSessions.length === 0)) {
            <button type="button" class="btn btn-primary" (click)="startOpen()">{{ 'cash.open' | transloco }}</button>
          }
        }

        <h3>{{ 'cash.history' | transloco }}</h3>
        <div class="card table-card">
          <table class="table">
            <thead>
              <tr>
                <th>{{ 'cash.openedAt' | transloco }}</th>
                <th>{{ 'cash.closedAt' | transloco }}</th>
                <th>{{ 'cash.cashier' | transloco }}</th>
                <th class="num">{{ 'cash.expected' | transloco }}</th>
                <th class="num">{{ 'cash.counted' | transloco }}</th>
                <th class="num">{{ 'cash.difference' | transloco }}</th>
                <th>{{ 'common.status' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (s of history.items(); track s.id) {
                <tr>
                  <td>{{ s.openedAt | fcDateTime }}</td>
                  <td>{{ s.closedAt | fcDateTime }}</td>
                  <td>{{ s.openedByName ?? '—' }}</td>
                  <td class="num">{{ s.expectedAmount | fcMoney }}</td>
                  <td class="num">{{ s.countedAmount === null ? '—' : (s.countedAmount | fcMoney) }}</td>
                  <td class="num">{{ s.difference === null ? '—' : (s.difference | fcMoney) }}</td>
                  <td><fc-badge [tone]="s.status === 'Open' ? 'ok' : 'off'">{{ 'enums.cashSessionStatus.' + s.status | transloco }}</fc-badge></td>
                </tr>
              } @empty {
                <tr><td colspan="7" class="empty">{{ 'cash.noHistory' | transloco }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <fc-pager [page]="history.page()" [pageSize]="history.pageSize" [total]="history.total()" (pageChange)="history.goTo($event)" />
      </section>
    }

    @switch (dialog()) {
      @case ('open') {
        <fc-modal [title]="'cash.open' | transloco" (closed)="closeDialog()">
          <form id="cash-form" [formGroup]="openForm" (ngSubmit)="submitOpen()" novalidate>
            @if (dialogError()) {
              <p class="alert alert-danger" role="alert">{{ dialogError() }}</p>
            }
            <label class="field">
              <span>{{ 'cash.openingAmount' | transloco }}</span>
              <input type="number" min="0" step="0.01" formControlName="amount" />
            </label>
            <label class="field">
              <span>{{ 'cash.openNotes' | transloco }}</span>
              <textarea formControlName="notes" maxlength="300"></textarea>
            </label>
          </form>
          <ng-container footer>
            <button type="button" class="btn" (click)="closeDialog()">{{ 'common.cancel' | transloco }}</button>
            <button type="submit" form="cash-form" class="btn btn-primary" [disabled]="busy()">{{ 'cash.open' | transloco }}</button>
          </ng-container>
        </fc-modal>
      }
      @case ('close') {
        <fc-modal [title]="'cash.close' | transloco" (closed)="closeDialog()">
          @if (dialogError()) {
            <p class="alert alert-danger" role="alert">{{ dialogError() }}</p>
          }
          @if (confirmingClose()) {
            <p class="alert alert-warning" role="alert">{{ closeMessage() }}</p>
          }
          <form id="cash-form" [formGroup]="closeForm" (ngSubmit)="submitClose()" novalidate [hidden]="confirmingClose()">
            @if (closing(); as s) {
              <dl class="kv">
                <dt>{{ 'cash.expected' | transloco }}</dt>
                <dd><strong>{{ s.expectedAmount | fcMoney }}</strong> <small class="muted">{{ 'cash.expectedHint' | transloco }}</small></dd>
              </dl>
            }
            <label class="field">
              <span>{{ 'cash.counted' | transloco }}</span>
              <input type="number" min="0" step="0.01" formControlName="counted" />
            </label>
            @if (difference() !== null) {
              <p class="alert" [class.alert-warning]="difference() !== 0" [class.alert-info]="difference() === 0">
                {{ difference() === 0 ? ('cash.exact' | transloco) : (difference()! < 0 ? ('cash.shortage' | transloco) : ('cash.surplus' | transloco)) }}
                @if (difference() !== 0) {
                  : {{ absDifference() | fcMoney }}
                }
              </p>
            }
            <label class="field">
              <span>{{ 'cash.closeNotes' | transloco }}</span>
              <textarea formControlName="notes" maxlength="300"></textarea>
              @if (difference() !== null && difference() !== 0) {
                <small class="muted">{{ 'cash.closeNotesRequired' | transloco }}</small>
              }
            </label>
          </form>
          <ng-container footer>
            @if (confirmingClose()) {
              <button type="button" class="btn" [disabled]="busy()" (click)="confirmingClose.set(false)">{{ 'common.back' | transloco }}</button>
              <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="submitClose()">{{ 'cash.close' | transloco }}</button>
            } @else {
              <button type="button" class="btn" (click)="closeDialog()">{{ 'common.cancel' | transloco }}</button>
              <button type="submit" form="cash-form" class="btn btn-primary" [disabled]="busy()">{{ 'common.review' | transloco }}</button>
            }
          </ng-container>
        </fc-modal>
      }
      @case ('movement') {
        <fc-modal [title]="'cash.newMovement' | transloco" (closed)="closeDialog()">
          <form id="cash-form" [formGroup]="movementForm" (ngSubmit)="submitMovement()" novalidate>
            @if (dialogError()) {
              <p class="alert alert-danger" role="alert">{{ dialogError() }}</p>
            }
            <label class="field">
              <span>{{ 'cash.movementType' | transloco }}</span>
              <select formControlName="type">
                <option value="ManualIn">{{ 'enums.cashMovementType.ManualIn' | transloco }}</option>
                <option value="ManualOut">{{ 'enums.cashMovementType.ManualOut' | transloco }}</option>
              </select>
            </label>
            <label class="field">
              <span>{{ 'common.amount' | transloco }}</span>
              <input type="number" min="0.01" step="0.01" formControlName="amount" />
            </label>
            <label class="field">
              <span>{{ 'cash.movementReason' | transloco }}</span>
              <textarea formControlName="reason" maxlength="300"></textarea>
            </label>
          </form>
          <ng-container footer>
            <button type="button" class="btn" (click)="closeDialog()">{{ 'common.cancel' | transloco }}</button>
            <button type="submit" form="cash-form" class="btn btn-primary" [disabled]="busy()">{{ 'common.save' | transloco }}</button>
          </ng-container>
        </fc-modal>
      }
      @case ('register') {
        <fc-modal [title]="(editingRegister() === 'new' ? 'cash.newRegister' : 'cash.editRegister') | transloco" (closed)="closeDialog()">
          <form id="cash-form" [formGroup]="registerForm" (ngSubmit)="submitRegister()" novalidate>
            @if (dialogError()) {
              <p class="alert alert-danger" role="alert">{{ dialogError() }}</p>
            }
            @if (editingRegister() === 'new') {
              <label class="field">
                <span>{{ 'common.branch' | transloco }}</span>
                <select formControlName="branchId">
                  @for (branch of catalog.branches(); track branch.id) {
                    <option [value]="branch.id">{{ branch.name }}</option>
                  }
                </select>
              </label>
            }
            <label class="field">
              <span>{{ 'cash.name' | transloco }}</span>
              <input formControlName="name" maxlength="80" />
            </label>
            <label class="check">
              <input type="checkbox" formControlName="allowMultipleOpenSessions" />
              <span>{{ 'cash.allowMultiple' | transloco }}</span>
            </label>
            @if (editingRegister() !== 'new') {
              <label class="check">
                <input type="checkbox" formControlName="isActive" />
                <span>{{ 'cash.active' | transloco }}</span>
              </label>
            }
          </form>
          <ng-container footer>
            <button type="button" class="btn" (click)="closeDialog()">{{ 'common.cancel' | transloco }}</button>
            <button type="submit" form="cash-form" class="btn btn-primary" [disabled]="busy()">{{ 'common.save' | transloco }}</button>
          </ng-container>
        </fc-modal>
      }
    }
  `,
})
export class Cash implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly format = inject(Formatter);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly catalog = inject(BranchCatalog);

  protected readonly registers = signal<CashRegister[]>([]);
  protected readonly selected = signal<CashRegister | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly dialog = signal<Dialog>(null);
  protected readonly dialogError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly closing = signal<CashSession | null>(null);
  protected readonly confirmingClose = signal(false);
  protected readonly editingRegister = signal<CashRegister | 'new'>('new');

  protected readonly canManage = computed(() => this.session.has(Perm.CashManage));
  protected readonly canOperate = computed(() => this.session.has(Perm.CashOperate));

  /** Sesión abierta que se muestra: la propia si existe; si no, la única abierta de la caja. */
  protected readonly activeSession = computed(() => {
    const open = this.selected()?.openSessions ?? [];
    const me = this.session.user()?.userId;
    return open.find((s) => s.openedBy === me) ?? open[0] ?? null;
  });

  protected movements!: ListState<CashMovement>;
  protected history!: ListState<CashSession>;

  protected readonly openForm = this.fb.nonNullable.group({ amount: [0, [Validators.required, Validators.min(0)]], notes: [''] });
  protected readonly closeForm = this.fb.nonNullable.group({ counted: [0, [Validators.required, Validators.min(0)]], notes: [''] });
  protected readonly movementForm = this.fb.nonNullable.group({
    type: ['ManualOut' as 'ManualIn' | 'ManualOut'],
    amount: [0, [Validators.required, Validators.min(0.01)]],
    reason: ['', [Validators.required, Validators.minLength(5)]],
  });
  protected readonly registerForm = this.fb.nonNullable.group({
    branchId: [''],
    name: ['', [Validators.required, Validators.maxLength(80)]],
    allowMultipleOpenSessions: [false],
    isActive: [true],
  });

  private readonly counted = signal(0);
  protected readonly difference = computed(() => {
    const s = this.closing();
    return s ? Math.round((this.counted() - s.expectedAmount) * 100) / 100 : null;
  });
  protected readonly absDifference = computed(() => Math.abs(this.difference() ?? 0));

  private readonly attempt = new AttemptKey();

  constructor() {
    const registerId = (): string => this.selected()?.id ?? '';
    this.movements = new ListState<CashMovement>(
      (query) => this.api.cashMovements(registerId(), { ...query, sessionId: this.activeSession()?.id }),
      this.errors,
      this.destroyRef,
      10,
    );
    this.history = new ListState<CashSession>((query) => this.api.cashSessions(registerId(), { ...query, status: 'Closed' }), this.errors, this.destroyRef, 10);
    this.closeForm.controls.counted.valueChanges.subscribe((value) => this.counted.set(Number(value) || 0));
  }

  ngOnInit(): void {
    this.catalog.ensure();
    this.loadRegisters();
  }

  protected select(register: CashRegister): void {
    this.selected.set(register);
    this.movements.load();
    this.history.load();
  }

  protected openRegisterForm(target: CashRegister | 'new'): void {
    this.dialogError.set(null);
    this.editingRegister.set(target);
    if (target === 'new') {
      this.registerForm.reset({ branchId: this.catalog.branches()[0]?.id ?? '', name: '', allowMultipleOpenSessions: false, isActive: true });
    } else {
      this.registerForm.reset({ branchId: target.branchId, name: target.name, allowMultipleOpenSessions: target.allowMultipleOpenSessions, isActive: target.isActive });
    }
    this.dialog.set('register');
  }

  protected startOpen(): void {
    this.attempt.renew();
    this.dialogError.set(null);
    this.openForm.reset({ amount: 0, notes: '' });
    this.dialog.set('open');
  }

  protected startClose(session: CashSession): void {
    this.attempt.renew();
    this.dialogError.set(null);
    this.closing.set(session);
    this.confirmingClose.set(false);
    this.counted.set(0);
    this.closeForm.reset({ counted: 0, notes: '' });
    this.dialog.set('close');
  }

  protected startMovement(): void {
    this.attempt.renew();
    this.dialogError.set(null);
    this.movementForm.reset({ type: 'ManualOut', amount: 0, reason: '' });
    this.dialog.set('movement');
  }

  protected closeMessage(): string {
    return this.errors.text('cash.closeConfirm', { counted: this.format.money(Number(this.closeForm.controls.counted.value) || 0) });
  }

  protected closeDialog(): void {
    this.confirmingClose.set(false);
    this.dialog.set(null);
    this.closing.set(null);
  }

  protected submitOpen(): void {
    const reg = this.selected();
    if (!reg || this.openForm.invalid) {
      this.openForm.markAllAsTouched();
      return;
    }
    const v = this.openForm.getRawValue();
    this.perform(this.api.openCash(reg.id, { openingAmount: Number(v.amount), notes: v.notes.trim() || null }, this.attempt.value), 'cash.opened', true);
  }

  protected submitClose(): void {
    const reg = this.selected();
    const session = this.closing();
    if (!reg || !session || this.closeForm.invalid) {
      this.closeForm.markAllAsTouched();
      return;
    }
    const v = this.closeForm.getRawValue();
    if (this.difference() !== 0 && v.notes.trim().length === 0) {
      this.dialogError.set(this.errors.text('cash.closeNotesRequired'));
      return;
    }
    // Cierre definitivo: primero se muestra el resumen y solo la segunda pulsación lo envía.
    if (!this.confirmingClose()) {
      this.dialogError.set(null);
      this.confirmingClose.set(true);
      return;
    }
    this.perform(
      this.api.closeCash(reg.id, { sessionId: session.id, countedAmount: Number(v.counted), notes: v.notes.trim() || null }, this.attempt.value),
      'cash.closed',
      true,
    );
  }

  protected submitMovement(): void {
    const reg = this.selected();
    if (!reg || this.movementForm.invalid) {
      this.movementForm.markAllAsTouched();
      this.dialogError.set(this.errors.text('cash.invalidForm'));
      return;
    }
    const v = this.movementForm.getRawValue();
    this.perform(this.api.addCashMovement(reg.id, { type: v.type, amount: Number(v.amount), reason: v.reason.trim() }, this.attempt.value), 'cash.movementRegistered', true);
  }

  protected submitRegister(): void {
    if (this.registerForm.invalid) {
      this.registerForm.markAllAsTouched();
      this.dialogError.set(this.errors.text('cash.invalidForm'));
      return;
    }
    const v = this.registerForm.getRawValue();
    const target = this.editingRegister();
    const request =
      target === 'new'
        ? this.api.createCashRegister({ branchId: v.branchId, name: v.name.trim(), allowMultipleOpenSessions: v.allowMultipleOpenSessions })
        : this.api.updateCashRegister(target.id, { name: v.name.trim(), isActive: v.isActive, allowMultipleOpenSessions: v.allowMultipleOpenSessions });
    this.perform(request, 'cash.registerSavedToast', false);
  }

  /** El aviso de éxito y el refresco ocurren solo después de la respuesta del servidor; en error el diálogo sigue abierto. */
  private perform(request: Observable<unknown>, toastKey: string, keyed: boolean): void {
    if (this.busy()) {
      return;
    }
    this.busy.set(true);
    this.dialogError.set(null);
    request.subscribe({
      next: () => {
        this.busy.set(false);
        if (keyed) {
          this.attempt.renew();
        }
        this.closeDialog();
        this.toast.success(toastKey);
        this.loadRegisters(true);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        if (keyed) {
          this.attempt.afterError(err);
        }
        this.dialogError.set(this.errors.message(err));
      },
    });
  }

  private loadRegisters(refreshSelected = false): void {
    this.api.cashRegisters({ pageSize: 100 }).subscribe({
      next: (page) => {
        this.registers.set(page.items);
        const current = this.selected();
        if (current) {
          this.selected.set(page.items.find((r) => r.id === current.id) ?? null);
        }
        if (refreshSelected && this.selected()) {
          this.movements.load();
          this.history.load();
        }
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }
}
