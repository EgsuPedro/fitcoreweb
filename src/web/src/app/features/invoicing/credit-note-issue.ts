import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { IssueCreditNoteRequest, SriBuyerIdType, SriCreditNotePreview } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { FcMoney } from '../../shared/format.pipes';
import { ToastService } from '../../shared/toast';

/** Los tipos que se pueden elegir para quien recibe la nota: nunca consumidor final (el SRI exige identificar al receptor). */
export const CREDIT_BUYER_TYPES: readonly SriBuyerIdType[] = ['Cedula', 'Ruc', 'Passport', 'Foreign'];

/** Importe escrito por la persona: acepta coma o punto decimal y como máximo dos decimales. Devuelve nulo si no es un importe válido (mayor que cero). */
export function parseAmount(text: string): number | null {
  const normalized = text.trim().replace(',', '.');
  if (!/^\d+(\.\d{1,2})?$/.test(normalized)) {
    return null;
  }
  const value = Number(normalized);
  return value > 0 ? value : null;
}

/** Redondea a centavos para comparar importes sin arrastrar errores de coma flotante. */
export const cents = (value: number): number => Math.round(value * 100);

export interface LineChoice {
  included: boolean;
  amount: string;
}

/** Qué se pide acreditar: solo las líneas incluidas, cada una con su importe. Nulo si falta o sobra algo (importe inválido o mayor que lo que queda, o ninguna línea). */
export function buildCreditLines(preview: Pick<SriCreditNotePreview, 'lines'>, choices: Record<string, LineChoice>): { lineId: string; amount: number }[] | null {
  const lines: { lineId: string; amount: number }[] = [];
  for (const line of preview.lines) {
    const choice = choices[line.lineId];
    if (!choice?.included) {
      continue;
    }
    const amount = parseAmount(choice.amount);
    if (amount === null || cents(amount) > cents(line.remainingAmount)) {
      return null;
    }
    lines.push({ lineId: line.lineId, amount });
  }
  return lines.length > 0 ? lines : null;
}

/**
 * Emitir una nota de crédito sobre una factura autorizada. Se elige qué líneas acreditar y cuánto (con IVA incluido, como se vende); por omisión, todo lo que queda de cada una. El IVA es siempre el de la
 * factura. Si la factura fue a consumidor final hay que identificar a quien recibe la nota. Emitir asigna número y clave; «enviar al SRI» además la pone en la cola, de donde el sistema la firma y la transmite.
 */
@Component({
  selector: 'fc-credit-note-issue',
  imports: [RouterLink, TranslocoPipe, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a [routerLink]="['/invoicing/invoices', invoiceId]">← {{ 'invoicing.creditNote.back' | transloco }}</a></p>
    <header class="page-header">
      <div>
        <h1>{{ 'invoicing.creditNote.title' | transloco }}</h1>
        <p class="muted">{{ 'invoicing.creditNote.subtitle' | transloco }}</p>
      </div>
    </header>

    @if (loadError()) { <p class="alert alert-danger" role="alert">{{ loadError() }}</p> }
    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    @if (preview(); as p) {
      @if (!p.canIssue) {
        <section class="alert alert-danger" role="alert">
          <ul>@for (i of p.issues; track i.code) { @if (i.blocking) { <li>{{ 'invoicing.creditNote.issues.' + i.code | transloco }}</li> } }</ul>
        </section>
      }

      <section class="card section">
        <h2>{{ 'invoicing.creditNote.invoice' | transloco: { number: p.number } }}</h2>
        <dl class="kv">
          <dt>{{ 'invoicing.detail.name' | transloco }}</dt><dd>{{ p.buyerName }}</dd>
          <dt>{{ 'invoicing.detail.total' | transloco }}</dt><dd>{{ p.total | fcMoney }}</dd>
          <dt>{{ 'invoicing.creditNote.credited' | transloco }}</dt><dd>{{ p.creditedTotal | fcMoney }}</dd>
          <dt>{{ 'invoicing.creditNote.remaining' | transloco }}</dt><dd><strong>{{ p.remainingTotal | fcMoney }}</strong></dd>
        </dl>
      </section>

      <section class="card section">
        <h2>{{ 'invoicing.creditNote.lines' | transloco }}</h2>
        <p class="muted">{{ 'invoicing.creditNote.linesHelp' | transloco }}</p>
        <div class="table-card">
          <table class="table">
            <caption class="sr-only">{{ 'invoicing.creditNote.lines' | transloco }}</caption>
            <thead>
              <tr>
                <th scope="col"><span class="sr-only">{{ 'invoicing.creditNote.include' | transloco }}</span></th>
                <th scope="col">{{ 'invoicing.detail.description' | transloco }}</th>
                <th scope="col" class="num">{{ 'invoicing.creditNote.original' | transloco }}</th>
                <th scope="col" class="num">{{ 'invoicing.creditNote.credited' | transloco }}</th>
                <th scope="col" class="num">{{ 'invoicing.creditNote.remaining' | transloco }}</th>
                <th scope="col" class="num">{{ 'invoicing.creditNote.amount' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (l of p.lines; track l.lineId) {
                <tr>
                  <td>
                    <input type="checkbox" [checked]="choice(l.lineId).included" [disabled]="l.remainingAmount <= 0" [attr.aria-label]="'invoicing.creditNote.include' | transloco"
                           (change)="setIncluded(l.lineId, $any($event.target).checked)" />
                  </td>
                  <td>{{ l.description }} <small class="muted">IVA {{ l.taxPercent }}%</small></td>
                  <td class="num">{{ l.originalAmount | fcMoney }}</td>
                  <td class="num">{{ l.creditedAmount | fcMoney }}</td>
                  <td class="num">{{ l.remainingAmount | fcMoney }}</td>
                  <td class="num">
                    <input class="amount" inputmode="decimal" autocomplete="off" [value]="choice(l.lineId).amount" [disabled]="!choice(l.lineId).included"
                           [attr.aria-label]="'invoicing.creditNote.amount' | transloco" [attr.aria-invalid]="amountInvalid(l.lineId, l.remainingAmount)"
                           (input)="setAmount(l.lineId, $any($event.target).value)" />
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <dl class="kv totals">
          <dt><strong>{{ 'invoicing.creditNote.total' | transloco }}</strong></dt><dd class="num"><strong>{{ total() | fcMoney }}</strong></dd>
        </dl>
      </section>

      @if (p.buyerIsFinalConsumer) {
        <section class="card section">
          <h2>{{ 'invoicing.creditNote.receiver' | transloco }}</h2>
          <p class="alert alert-warning">{{ 'invoicing.creditNote.receiverHelp' | transloco }}</p>
          <div class="form-grid">
            <label class="field">
              <span>{{ 'invoicing.issue.idType' | transloco }}</span>
              <select [value]="buyer().idType" (change)="patchBuyer({ idType: $any($event.target).value })">
                @for (t of buyerTypes; track t) { <option [value]="t">{{ 'invoicing.idTypes.' + t | transloco }}</option> }
              </select>
            </label>
            <label class="field">
              <span>{{ 'invoicing.detail.idNumber' | transloco }}</span>
              <input [value]="buyer().idNumber" maxlength="20" autocomplete="off" (input)="patchBuyer({ idNumber: $any($event.target).value })" />
            </label>
            <label class="field">
              <span>{{ 'invoicing.detail.name' | transloco }}</span>
              <input [value]="buyer().legalName" maxlength="300" autocomplete="off" (input)="patchBuyer({ legalName: $any($event.target).value })" />
            </label>
            <label class="field">
              <span>{{ 'invoicing.detail.email' | transloco }}</span>
              <input type="email" [value]="buyer().email" maxlength="254" autocomplete="off" (input)="patchBuyer({ email: $any($event.target).value })" />
            </label>
            <label class="field">
              <span>{{ 'invoicing.detail.address' | transloco }}</span>
              <input [value]="buyer().address" maxlength="300" autocomplete="off" (input)="patchBuyer({ address: $any($event.target).value })" />
            </label>
          </div>
        </section>
      }

      <section class="card section">
        <label class="field">
          <span>{{ 'invoicing.creditNote.reason' | transloco }}</span>
          <textarea [value]="reason()" maxlength="300" (input)="reason.set($any($event.target).value)"></textarea>
          <small class="muted">{{ 'invoicing.creditNote.reasonHint' | transloco }}</small>
        </label>
      </section>

      <div class="inline-actions">
        <button type="button" class="btn btn-primary" [disabled]="busy() || !ready()" (click)="issue(true)">{{ 'invoicing.creditNote.issueAndSend' | transloco }}</button>
        <button type="button" class="btn" [disabled]="busy() || !ready()" (click)="issue(false)">{{ 'invoicing.issue.issueOnly' | transloco }}</button>
      </div>
      <p class="muted">{{ 'invoicing.creditNote.help' | transloco }}</p>
    }
  `,
})
export class CreditNoteIssuePage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  protected readonly buyerTypes = CREDIT_BUYER_TYPES;
  protected readonly preview = signal<SriCreditNotePreview | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly reason = signal('');
  protected readonly choices = signal<Record<string, LineChoice>>({});
  protected readonly buyer = signal({ idType: 'Cedula' as SriBuyerIdType, idNumber: '', legalName: '', email: '', address: '' });

  protected invoiceId = '';

  /** Suma de los importes escritos de las líneas incluidas (solo los válidos). */
  protected readonly total = computed(() => {
    const p = this.preview();
    const choices = this.choices();
    return p ? p.lines.reduce((sum, l) => sum + (choices[l.lineId]?.included ? (parseAmount(choices[l.lineId].amount) ?? 0) : 0), 0) : 0;
  });

  protected readonly ready = computed(() => {
    const p = this.preview();
    if (!p || !p.canIssue || this.reason().trim().length < 5 || buildCreditLines(p, this.choices()) === null) {
      return false;
    }
    if (p.buyerIsFinalConsumer) {
      const b = this.buyer();
      return b.idNumber.trim().length > 0 && b.legalName.trim().length > 0;
    }
    return true;
  });

  ngOnInit(): void {
    this.invoiceId = this.route.snapshot.paramMap.get('invoiceId') ?? '';
    this.api.creditNotePreview(this.invoiceId).subscribe({
      next: (p) => {
        this.preview.set(p);
        this.choices.set(Object.fromEntries(p.lines.map((l) => [l.lineId, { included: l.remainingAmount > 0, amount: l.remainingAmount.toFixed(2) }])));
      },
      error: (err: unknown) => this.loadError.set(this.errors.message(err)),
    });
  }

  protected choice(lineId: string): LineChoice {
    return this.choices()[lineId] ?? { included: false, amount: '' };
  }

  protected setIncluded(lineId: string, included: boolean): void {
    this.choices.update((c) => ({ ...c, [lineId]: { ...this.choice(lineId), included } }));
  }

  protected setAmount(lineId: string, amount: string): void {
    this.choices.update((c) => ({ ...c, [lineId]: { ...this.choice(lineId), amount } }));
  }

  protected amountInvalid(lineId: string, remaining: number): boolean {
    const choice = this.choice(lineId);
    if (!choice.included) {
      return false;
    }
    const amount = parseAmount(choice.amount);
    return amount === null || cents(amount) > cents(remaining);
  }

  protected patchBuyer(changes: Partial<ReturnType<typeof this.buyer>>): void {
    this.buyer.update((b) => ({ ...b, ...changes }));
  }

  protected issue(submit: boolean): void {
    const p = this.preview();
    const lines = p ? buildCreditLines(p, this.choices()) : null;
    if (!p || !lines) {
      return;
    }
    const b = this.buyer();
    const request: IssueCreditNoteRequest = {
      invoiceId: this.invoiceId,
      reason: this.reason().trim(),
      lines,
      submit,
      buyer: p.buyerIsFinalConsumer
        ? { idType: b.idType, idNumber: b.idNumber.trim(), legalName: b.legalName.trim(), email: b.email.trim() || null, address: b.address.trim() || null, phone: null }
        : undefined,
    };
    this.busy.set(true);
    this.error.set(null);
    this.api.issueCreditNote(request).subscribe({
      next: (note) => {
        this.busy.set(false);
        this.toast.success(submit ? 'invoicing.creditNote.issuedAndQueued' : 'invoicing.creditNote.issued');
        void this.router.navigate(['/invoicing/invoices', note.id]);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
