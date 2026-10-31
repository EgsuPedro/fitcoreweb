import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { Observable, interval } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import type { SriDocumentKind, SriInvoice } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, invoiceTone } from '../../shared/badge';
import { FcDateTime, FcDate, FcMoney } from '../../shared/format.pipes';
import { Modal } from '../../shared/modal';
import { ReasonDialog } from '../../shared/reason-dialog';
import { ToastService } from '../../shared/toast';
import { saveBlob } from '../analytics/export-button';

/** Mientras el comprobante está en trámite con el SRI, la pantalla se refresca sola. */
const ACTIVE_STATUSES = new Set(['PendingSignature', 'SignedDocument', 'PendingSubmission', 'Received', 'Processing']);

export function isActive(invoice: Pick<SriInvoice, 'status' | 'transmission'>): boolean {
  return ACTIVE_STATUSES.has(invoice.status) && !!invoice.transmission && !invoice.transmission.parked;
}

/** Descartar: devuelto o no autorizado, o uno que todavía no se envió (sin intercambios con el SRI). Igual que en el servidor, que es quien decide. */
export function canDiscard(invoice: Pick<SriInvoice, 'status' | 'sriAttempts'>): boolean {
  return (
    invoice.status === 'Rejected' ||
    invoice.status === 'NotAuthorized' ||
    ((invoice.status === 'PendingSignature' || invoice.status === 'SignedDocument') && invoice.sriAttempts.length === 0)
  );
}

/** Enviar (o reenviar) al SRI: lo que nunca se encoló, lo devuelto y lo estacionado. Lo que ya está en la cola no necesita nada. */
export function canSubmit(invoice: Pick<SriInvoice, 'status' | 'transmission'>): boolean {
  if (invoice.status === 'Rejected') {
    return true;
  }
  if (invoice.status === 'PendingSignature' || invoice.status === 'SignedDocument' || invoice.status === 'PendingSubmission') {
    return !invoice.transmission || invoice.transmission.parked;
  }
  return false;
}

/** Mensajes del último intercambio con el SRI (los que explican una devolución o un rechazo). */
export function lastSriMessages(invoice: Pick<SriInvoice, 'sriAttempts'>): { id?: string | null; text?: string | null; detail?: string | null }[] {
  return invoice.sriAttempts.find((a) => a.messages.length > 0)?.messages ?? [];
}

/**
 * Detalle de un comprobante electrónico: estado ante el SRI y qué falta, datos congelados del comprobante, intercambios con el SRI, correos enviados e historial. Desde aquí se envía al SRI,
 * se descargan el RIDE y los XML, se reenvía por correo y se descarta uno devuelto para volver a emitirlo.
 */
@Component({
  selector: 'fc-invoice-detail',
  imports: [RouterLink, FormsModule, TranslocoPipe, Badge, Modal, ReasonDialog, FcDate, FcDateTime, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a routerLink="/invoicing">← {{ 'invoicing.invoices.back' | transloco }}</a></p>
    @if (loadError()) { <p class="alert alert-danger" role="alert">{{ loadError() }}</p> }
    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    @if (invoice(); as i) {
      <header class="page-header">
        <div>
          <h1>{{ (i.documentType === 'CreditNote' ? 'invoicing.detail.creditNoteTitle' : 'invoicing.detail.title') | transloco: { number: i.number } }}</h1>
          <p>
            <fc-badge [tone]="tone(i.status)">{{ 'enums.invoiceStatus.' + i.status | transloco }}</fc-badge>
            @if (i.environment === 'Test') { <fc-badge tone="neutral">{{ 'invoicing.invoices.testBadge' | transloco }}</fc-badge> }
            <span class="muted"> {{ i.issueDate | fcDate }} · {{ i.sourceReference }}</span>
          </p>
        </div>
        <div class="inline-actions">
          @if (can(perm.InvoicingSend) && submittable()) {
            <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="submit()">
              {{ (i.status === 'Rejected' || i.transmission?.parked ? 'invoicing.detail.resubmit' : 'invoicing.detail.submit') | transloco }}
            </button>
          }
          @if (i.status === 'Authorized') {
            @if (i.documentType === 'Invoice' && can(perm.InvoicingCreditNote) && i.creditedTotal < i.calculation.total) {
              <a class="btn" [routerLink]="['/invoicing/credit-notes/new', i.id]">{{ 'invoicing.detail.issueCreditNote' | transloco }}</a>
            }
            @if (can(perm.InvoicingDownload)) {
              <button type="button" class="btn" [disabled]="busy()" (click)="downloadRide()">{{ 'invoicing.detail.ride' | transloco }}</button>
            }
            @if (can(perm.InvoicingResendEmail)) {
              <button type="button" class="btn" [disabled]="busy()" (click)="openEmail()">{{ 'invoicing.detail.sendEmail' | transloco }}</button>
            }
          }
          @if (can(perm.InvoicingVoid) && discardable()) {
            <button type="button" class="btn btn-danger" [disabled]="busy()" (click)="discarding.set(true)">{{ 'invoicing.detail.discard' | transloco }}</button>
          }
        </div>
      </header>

      @switch (i.status) {
        @case ('Authorized') {
          <p class="alert alert-success" role="status">
            {{ 'invoicing.detail.authorized' | transloco: { date: (i.authorization?.authorizedAt | fcDateTime) } }}
            <br /><small class="mono">{{ i.authorization?.number }}</small>
          </p>
        }
        @case ('Rejected') { <p class="alert alert-danger" role="alert">{{ 'invoicing.detail.rejectedHelp' | transloco }}</p> }
        @case ('NotAuthorized') { <p class="alert alert-danger" role="alert">{{ 'invoicing.detail.notAuthorizedHelp' | transloco }}</p> }
        @case ('Discarded') {
          <p class="alert alert-info" role="status">
            {{ 'invoicing.detail.discardedHelp' | transloco }}
            @if (i.documentType === 'Invoice' && i.sourceType === 'obligation' && can(perm.InvoicingIssue)) {
              <a class="btn" [routerLink]="['/invoicing/issue', i.sourceId]">{{ 'invoicing.detail.reissue' | transloco }}</a>
            }
            @if (i.documentType === 'CreditNote' && i.creditNote && can(perm.InvoicingCreditNote)) {
              <a class="btn" [routerLink]="['/invoicing/credit-notes/new', i.creditNote.modifiedInvoiceId]">{{ 'invoicing.detail.reissueCreditNote' | transloco }}</a>
            }
          </p>
        }
        @default {
          @if (i.transmission?.parked) {
            <p class="alert alert-warning" role="alert">{{ 'invoicing.detail.parkedHelp' | transloco }}</p>
          } @else if (active()) {
            <p class="alert alert-info" role="status">{{ 'invoicing.detail.inProgressHelp' | transloco }}</p>
          }
        }
      }

      @if (messages().length > 0 && (i.status === 'Rejected' || i.status === 'NotAuthorized')) {
        <section class="card section">
          <h2>{{ 'invoicing.detail.sriMessages' | transloco }}</h2>
          <ul>
            @for (m of messages(); track $index) {
              <li><strong>{{ m.id }}</strong> {{ m.text }} @if (m.detail) { <br /><small class="muted">{{ m.detail }}</small> }</li>
            }
          </ul>
        </section>
      }

      @if (i.documentType === 'CreditNote' && i.creditNote) {
        <section class="card section">
          <h2>{{ 'invoicing.detail.modifies' | transloco }}</h2>
          <dl class="kv">
            <dt>{{ 'enums.invoiceDocumentType.Invoice' | transloco }}</dt>
            <dd><a [routerLink]="['/invoicing/invoices', i.creditNote.modifiedInvoiceId]">{{ i.creditNote.modifiedNumber }}</a> · {{ i.creditNote.modifiedIssueDate | fcDate }}</dd>
            <dt>{{ 'invoicing.detail.reason' | transloco }}</dt><dd>{{ i.creditNote.reason }}</dd>
          </dl>
        </section>
      }

      <div class="grid-2">
        <section class="card section">
          <h2>{{ 'invoicing.detail.buyer' | transloco }}</h2>
          <dl class="kv">
            <dt>{{ 'invoicing.detail.name' | transloco }}</dt><dd>{{ i.buyer.legalName }}</dd>
            <dt>{{ 'invoicing.idTypes.' + i.buyer.idType | transloco }}</dt><dd>{{ i.buyer.idNumber }}</dd>
            @if (i.buyer.email) { <dt>{{ 'invoicing.detail.email' | transloco }}</dt><dd>{{ i.buyer.email }}</dd> }
            @if (i.buyer.phone) { <dt>{{ 'invoicing.detail.phone' | transloco }}</dt><dd>{{ i.buyer.phone }}</dd> }
            @if (i.buyer.address) { <dt>{{ 'invoicing.detail.address' | transloco }}</dt><dd>{{ i.buyer.address }}</dd> }
          </dl>
        </section>
        <section class="card section">
          <h2>{{ 'invoicing.detail.issuer' | transloco }}</h2>
          <dl class="kv">
            <dt>{{ 'invoicing.detail.name' | transloco }}</dt><dd>{{ i.issuer.legalName }}</dd>
            <dt>RUC</dt><dd>{{ i.issuer.ruc }}</dd>
            <dt>{{ 'invoicing.detail.accessKey' | transloco }}</dt><dd class="mono">{{ i.accessKey }}</dd>
          </dl>
        </section>
      </div>

      <section class="card section">
        <h2>{{ 'invoicing.detail.lines' | transloco }}</h2>
        <div class="table-card">
          <table class="table">
            <caption class="sr-only">{{ 'invoicing.detail.lines' | transloco }}</caption>
            <thead>
              <tr>
                <th scope="col">{{ 'invoicing.detail.description' | transloco }}</th>
                <th scope="col" class="num">{{ 'invoicing.detail.quantity' | transloco }}</th>
                <th scope="col" class="num">{{ 'invoicing.detail.unitPrice' | transloco }}</th>
                <th scope="col" class="num">{{ 'invoicing.detail.discount' | transloco }}</th>
                <th scope="col" class="num">{{ 'invoicing.detail.subtotal' | transloco }}</th>
                <th scope="col" class="num">IVA</th>
              </tr>
            </thead>
            <tbody>
              @for (l of i.calculation.lines; track l.lineNumber) {
                <tr>
                  <td>{{ l.description }}</td>
                  <td class="num">{{ l.quantity }}</td>
                  <td class="num">{{ l.unitPrice | fcMoney }}</td>
                  <td class="num">{{ l.discount | fcMoney }}</td>
                  <td class="num">{{ l.subtotal | fcMoney }}</td>
                  <td class="num">{{ l.taxPercent }}%</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <dl class="kv totals">
          <dt>{{ 'invoicing.detail.subtotalNoTax' | transloco }}</dt><dd class="num">{{ i.calculation.subtotalWithoutTaxes | fcMoney }}</dd>
          <dt>{{ 'invoicing.detail.discount' | transloco }}</dt><dd class="num">{{ i.calculation.totalDiscount | fcMoney }}</dd>
          @for (t of i.calculation.taxes; track t.rateCode) {
            @if (t.percent > 0) { <dt>IVA {{ t.percent }}%</dt><dd class="num">{{ t.amount | fcMoney }}</dd> }
          }
          <dt><strong>{{ 'invoicing.detail.total' | transloco }}</strong></dt><dd class="num"><strong>{{ i.calculation.total | fcMoney }}</strong></dd>
        </dl>
        @for (p of i.calculation.payments; track $index) {
          <p class="muted">{{ 'invoicing.detail.paidWith' | transloco }}: {{ 'enums.paymentMethod.' + p.method | transloco }} · {{ p.amount | fcMoney }}</p>
        }
      </section>

      @if (i.creditNotes.length > 0) {
        <section class="card section">
          <h2>{{ 'invoicing.detail.creditNotes' | transloco }}</h2>
          <p class="muted">{{ 'invoicing.detail.creditedOf' | transloco }}: <strong>{{ i.creditedTotal | fcMoney }}</strong> / {{ i.calculation.total | fcMoney }}</p>
          <div class="table-card">
            <table class="table">
              <caption class="sr-only">{{ 'invoicing.detail.creditNotes' | transloco }}</caption>
              <thead>
                <tr>
                  <th scope="col">{{ 'invoicing.invoices.number' | transloco }}</th>
                  <th scope="col">{{ 'invoicing.invoices.issueDate' | transloco }}</th>
                  <th scope="col">{{ 'invoicing.detail.reason' | transloco }}</th>
                  <th scope="col" class="num">{{ 'invoicing.invoices.total' | transloco }}</th>
                  <th scope="col">{{ 'common.status' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (n of i.creditNotes; track n.id) {
                  <tr>
                    <td><a [routerLink]="['/invoicing/invoices', n.id]">{{ n.number }}</a></td>
                    <td>{{ n.issueDate | fcDate }}</td>
                    <td>{{ n.reason }}</td>
                    <td class="num">{{ n.total | fcMoney }}</td>
                    <td><fc-badge [tone]="tone(n.status)">{{ 'enums.invoiceStatus.' + n.status | transloco }}</fc-badge></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }

      @if (i.emails.length > 0 || i.status === 'Authorized') {
        <section class="card section">
          <h2>{{ 'invoicing.detail.emails' | transloco }}</h2>
          <div class="table-card">
            <table class="table">
              <caption class="sr-only">{{ 'invoicing.detail.emails' | transloco }}</caption>
              <thead>
                <tr>
                  <th scope="col">{{ 'invoicing.detail.emailTo' | transloco }}</th>
                  <th scope="col">{{ 'common.status' | transloco }}</th>
                  <th scope="col">{{ 'invoicing.detail.when' | transloco }}</th>
                  <th scope="col">{{ 'invoicing.detail.origin' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (e of i.emails; track e.id) {
                  <tr>
                    <td>{{ e.to }}</td>
                    <td>
                      <fc-badge [tone]="e.status === 'Sent' ? 'ok' : e.status === 'Failed' ? 'danger' : 'warn'">{{ 'enums.invoiceEmailStatus.' + e.status | transloco }}</fc-badge>
                      @if (e.lastError && e.status !== 'Sent') { <br /><small class="muted">{{ e.lastError }}</small> }
                    </td>
                    <td>{{ (e.sentAt ?? e.createdAt) | fcDateTime }}</td>
                    <td>{{ (e.automatic ? 'invoicing.detail.automatic' : 'invoicing.detail.manual') | transloco }}</td>
                  </tr>
                } @empty {
                  <tr><td colspan="4" class="empty">{{ 'invoicing.detail.noEmails' | transloco }}</td></tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }

      <section class="card section">
        <h2>{{ 'invoicing.detail.sriExchanges' | transloco }}</h2>
        <div class="table-card">
          <table class="table">
            <caption class="sr-only">{{ 'invoicing.detail.sriExchanges' | transloco }}</caption>
            <thead>
              <tr>
                <th scope="col">{{ 'invoicing.detail.when' | transloco }}</th>
                <th scope="col">{{ 'invoicing.detail.operation' | transloco }}</th>
                <th scope="col">{{ 'invoicing.detail.outcome' | transloco }}</th>
                <th scope="col">{{ 'invoicing.detail.sriMessages' | transloco }}</th>
              </tr>
            </thead>
            <tbody>
              @for (a of i.sriAttempts; track $index) {
                <tr>
                  <td>{{ a.at | fcDateTime }}</td>
                  <td>{{ 'invoicing.detail.operations.' + a.operation | transloco }}</td>
                  <td>{{ 'invoicing.detail.outcomes.' + a.outcome | transloco }}@if (a.errorCode) { <br /><small class="muted">{{ a.errorCode }}</small> }</td>
                  <td>@for (m of a.messages; track $index) { <div><strong>{{ m.id }}</strong> {{ m.text }}</div> } @empty { — }</td>
                </tr>
              } @empty {
                <tr><td colspan="4" class="empty">{{ 'invoicing.detail.noExchanges' | transloco }}</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>

      @if (can(perm.InvoicingDownload) && i.documents.length > 0) {
        <section class="card section">
          <h2>{{ 'invoicing.detail.documents' | transloco }}</h2>
          <div class="inline-actions">
            @for (d of i.documents; track d.kind) {
              <button type="button" class="btn" [disabled]="busy()" (click)="downloadXml(d.kind)">{{ 'invoicing.detail.docKinds.' + d.kind | transloco }}</button>
            }
          </div>
          <p class="muted">{{ 'invoicing.detail.documentsHint' | transloco }}</p>
        </section>
      }

      <section class="card section">
        <h2>{{ 'invoicing.detail.history' | transloco }}</h2>
        <ol class="timeline">
          @for (e of i.events; track $index) {
            <li>
              <strong>{{ 'invoicing.detail.events.' + e.type | transloco }}</strong>
              <span class="muted"> · {{ e.at | fcDateTime }}</span>
              @if (e.detail) { <br /><small>{{ e.detail }}</small> }
            </li>
          }
        </ol>
      </section>

      @if (emailEditor()) {
        <fc-modal [title]="'invoicing.detail.sendEmail' | transloco" (closed)="emailEditor.set(false)">
          @if (formError()) { <p class="alert alert-danger" role="alert">{{ formError() }}</p> }
          <p class="muted">{{ 'invoicing.detail.sendEmailHelp' | transloco }}</p>
          <label class="field">
            <span>{{ 'invoicing.detail.emailTo' | transloco }}</span>
            <input type="email" name="to" maxlength="256" autocomplete="off" [ngModel]="emailTo()" (ngModelChange)="emailTo.set($event)" />
          </label>
          <ng-container footer>
            <button type="button" class="btn" (click)="emailEditor.set(false)">{{ 'common.cancel' | transloco }}</button>
            <button type="button" class="btn btn-primary" [disabled]="busy() || !emailTo().trim()" (click)="sendEmail()">{{ 'invoicing.detail.sendEmailConfirm' | transloco }}</button>
          </ng-container>
        </fc-modal>
      }

      @if (discarding()) {
        <fc-reason-dialog
          [title]="'invoicing.detail.discardTitle' | transloco"
          [message]="'invoicing.detail.discardHelp' | transloco"
          [label]="'invoicing.detail.discardReason' | transloco"
          [hint]="'invoicing.detail.discardHint' | transloco"
          [confirmLabel]="'invoicing.detail.discardConfirm' | transloco"
          [minLength]="10"
          [danger]="true"
          [busy]="busy()"
          [error]="formError()"
          (submitted)="discard($event)"
          (cancelled)="discarding.set(false)"
        />
      }
    }
  `,
})
export class InvoiceDetailPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly perm = Perm;
  protected readonly invoice = signal<SriInvoice | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly emailEditor = signal(false);
  protected readonly emailTo = signal('');
  protected readonly discarding = signal(false);
  protected readonly tone = invoiceTone;

  protected readonly active = computed(() => !!this.invoice() && isActive(this.invoice()!));
  protected readonly submittable = computed(() => !!this.invoice() && canSubmit(this.invoice()!));
  protected readonly discardable = computed(() => !!this.invoice() && canDiscard(this.invoice()!));
  protected readonly messages = computed(() => (this.invoice() ? lastSriMessages(this.invoice()!) : []));

  private id = '';

  ngOnInit(): void {
    this.id = this.route.snapshot.paramMap.get('id') ?? '';
    this.reload();
    // Mientras el comprobante está en trámite, se refresca solo para ver cuándo el SRI lo autoriza.
    interval(10_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        if (this.active() && !this.busy()) {
          this.reload();
        }
      });
  }

  protected can(permission: string): boolean {
    return this.session.has(permission);
  }

  private reload(): void {
    this.api.sriInvoice(this.id).subscribe({
      next: (i) => {
        this.invoice.set(i);
        this.loadError.set(null);
      },
      error: (err: unknown) => this.loadError.set(this.errors.message(err)),
    });
  }

  private run(call: Observable<SriInvoice>, toastKey: string, after?: () => void): void {
    this.busy.set(true);
    this.error.set(null);
    this.formError.set(null);
    call.subscribe({
      next: (i) => {
        this.busy.set(false);
        this.invoice.set(i);
        after?.();
        this.toast.success(toastKey);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        const message = this.errors.message(err);
        (this.emailEditor() || this.discarding() ? this.formError : this.error).set(message);
      },
    });
  }

  protected submit(): void {
    this.run(this.api.submitSriInvoice(this.id), 'invoicing.detail.submitted');
  }

  protected discard(reason: string): void {
    this.run(this.api.discardSriInvoice(this.id, reason), 'invoicing.detail.discarded', () => this.discarding.set(false));
  }

  protected openEmail(): void {
    this.formError.set(null);
    this.emailTo.set(this.invoice()?.buyer.email ?? '');
    this.emailEditor.set(true);
  }

  protected sendEmail(): void {
    this.run(this.api.resendSriInvoiceEmail(this.id, this.emailTo().trim()), 'invoicing.detail.emailQueued', () => this.emailEditor.set(false));
  }

  protected downloadRide(): void {
    const number = this.invoice()?.number ?? this.id;
    const prefix = this.invoice()?.documentType === 'CreditNote' ? 'NotaCredito' : 'Factura';
    this.download(this.api.sriInvoiceRide(this.id), `${prefix}-${number}.pdf`);
  }

  protected downloadXml(kind: string): void {
    const key = this.invoice()?.accessKey ?? this.id;
    const suffix = kind === 'Xml' ? '-sin-firma' : kind === 'AuthorizedXml' ? '-autorizado' : '';
    this.download(this.api.sriInvoiceXml(this.id, kind as SriDocumentKind), `${key}${suffix}.xml`);
  }

  private download(call: Observable<Blob>, fileName: string): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: (blob) => {
        this.busy.set(false);
        saveBlob(blob, fileName);
        // La descarga deja constancia en el historial: se vuelve a leer.
        this.reload();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
