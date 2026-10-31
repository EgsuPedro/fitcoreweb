import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { SriBuyerIdType, SriInvoicePreview } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { FcDate, FcDateTime, FcMoney } from '../../shared/format.pipes';
import { ToastService } from '../../shared/toast';

export const BUYER_ID_TYPES: readonly SriBuyerIdType[] = ['Cedula', 'Ruc', 'Passport', 'Foreign', 'FinalConsumer'];

/** Los avisos que no impiden emitir (por ejemplo, el comprador sin correo) se muestran aparte de los que sí. */
export function splitIssues(preview: Pick<SriInvoicePreview, 'issues'>): { blocking: string[]; warnings: string[] } {
  return {
    blocking: preview.issues.filter((i) => i.blocking).map((i) => i.code),
    warnings: preview.issues.filter((i) => !i.blocking).map((i) => i.code),
  };
}

/**
 * Emitir la factura electrónica de una venta cobrada. Muestra primero lo que se facturaría —comprador con su tipo de identificación según el SRI, líneas, IVA y totales— y todo lo que impide
 * emitir; nada se guarda hasta que se confirma. Emitir asigna número y clave de acceso; «enviar al SRI» además lo pone en la cola, de donde el sistema lo firma y lo transmite.
 */
@Component({
  selector: 'fc-invoice-issue',
  imports: [RouterLink, TranslocoPipe, FcDate, FcDateTime, FcMoney],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <p><a routerLink="/invoicing">← {{ 'invoicing.invoices.back' | transloco }}</a></p>
    <header class="page-header">
      <div>
        <h1>{{ 'invoicing.issue.title' | transloco }}</h1>
        <p class="muted">{{ 'invoicing.issue.subtitle' | transloco }}</p>
      </div>
    </header>

    @if (loadError()) { <p class="alert alert-danger" role="alert">{{ loadError() }}</p> }
    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    @if (preview(); as p) {
      @if (p.existingInvoiceId) {
        <p class="alert alert-info" role="status">
          {{ 'invoicing.issue.alreadyIssued' | transloco }}
          <a class="btn" [routerLink]="['/invoicing/invoices', p.existingInvoiceId]">{{ 'invoicing.issue.openInvoice' | transloco }}</a>
        </p>
      }

      @if (issues().blocking.length > 0 && !p.existingInvoiceId) {
        <section class="alert alert-danger" role="alert">
          <strong>{{ 'invoicing.issue.cannotIssue' | transloco }}</strong>
          <ul>
            @for (code of issues().blocking; track code) { <li>{{ 'invoicing.issues.' + code | transloco: { max: p.finalConsumerMaxAmount } }}</li> }
          </ul>
        </section>
      }
      @if (issues().warnings.length > 0) {
        <section class="alert alert-warning" role="status">
          <ul>
            @for (code of issues().warnings; track code) { <li>{{ 'invoicing.issues.' + code | transloco }}</li> }
          </ul>
        </section>
      }

      <div class="grid-2">
        <section class="card section">
          <h2>{{ 'invoicing.issue.sale' | transloco }}</h2>
          <dl class="kv">
            <dt>{{ 'invoicing.issue.reference' | transloco }}</dt><dd>{{ p.reference }}</dd>
            <dt>{{ 'invoicing.issue.issueDate' | transloco }}</dt><dd>{{ p.issueDate | fcDate }}</dd>
            <dt>{{ 'invoicing.issue.payable' | transloco }}</dt><dd>{{ p.payableTotal | fcMoney }}</dd>
            <dt>{{ 'invoicing.issue.paid' | transloco }}</dt><dd>{{ p.paidTotal | fcMoney }}</dd>
            @if (p.emissionPoint) { <dt>{{ 'invoicing.issue.point' | transloco }}</dt><dd>{{ p.emissionPoint }}</dd> }
          </dl>
        </section>

        <section class="card section">
          <h2>{{ 'invoicing.issue.buyer' | transloco }}</h2>
          <label class="field">
            <span>{{ 'invoicing.issue.idType' | transloco }}</span>
            <select [value]="buyerType() ?? p.buyer?.idType ?? ''" [disabled]="loading() || !!p.existingInvoiceId" (change)="changeType($any($event.target).value)">
              @for (t of types; track t) { <option [value]="t">{{ 'invoicing.idTypes.' + t | transloco }}</option> }
            </select>
            <small class="muted">{{ 'invoicing.issue.idTypeHint' | transloco }}</small>
          </label>
          @if (p.buyer; as b) {
            <dl class="kv">
              <dt>{{ 'invoicing.detail.name' | transloco }}</dt><dd>{{ b.legalName }}</dd>
              <dt>{{ 'invoicing.detail.idNumber' | transloco }}</dt><dd>{{ b.idNumber }}</dd>
              <dt>{{ 'invoicing.detail.email' | transloco }}</dt><dd>{{ b.email ?? '—' }}</dd>
              <dt>{{ 'invoicing.detail.address' | transloco }}</dt><dd>{{ b.address ?? '—' }}</dd>
            </dl>
          }
        </section>
      </div>

      @if (p.calculation; as c) {
        <section class="card section">
          <h2>{{ 'invoicing.issue.detail' | transloco }}</h2>
          <div class="table-card">
            <table class="table">
              <caption class="sr-only">{{ 'invoicing.issue.detail' | transloco }}</caption>
              <thead>
                <tr>
                  <th scope="col">{{ 'invoicing.detail.description' | transloco }}</th>
                  <th scope="col" class="num">{{ 'invoicing.detail.quantity' | transloco }}</th>
                  <th scope="col" class="num">{{ 'invoicing.detail.subtotal' | transloco }}</th>
                  <th scope="col" class="num">IVA</th>
                  <th scope="col" class="num">{{ 'invoicing.detail.total' | transloco }}</th>
                </tr>
              </thead>
              <tbody>
                @for (l of c.lines; track l.lineNumber) {
                  <tr>
                    <td>{{ l.description }}</td>
                    <td class="num">{{ l.quantity }}</td>
                    <td class="num">{{ l.subtotal | fcMoney }}</td>
                    <td class="num">{{ l.taxPercent }}%</td>
                    <td class="num">{{ l.subtotal + l.taxAmount | fcMoney }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <dl class="kv totals">
            <dt>{{ 'invoicing.detail.subtotalNoTax' | transloco }}</dt><dd class="num">{{ c.subtotalWithoutTaxes | fcMoney }}</dd>
            @for (t of c.taxes; track t.rateCode) {
              @if (t.percent > 0) { <dt>IVA {{ t.percent }}%</dt><dd class="num">{{ t.amount | fcMoney }}</dd> }
            }
            <dt><strong>{{ 'invoicing.detail.total' | transloco }}</strong></dt><dd class="num"><strong>{{ c.total | fcMoney }}</strong></dd>
          </dl>
          @if (c.roundingDifference !== 0) { <p class="muted">{{ 'invoicing.issue.rounding' | transloco: { amount: c.roundingDifference } }}</p> }
        </section>
      }

      @if (p.payments.length > 0) {
        <section class="card section">
          <h2>{{ 'invoicing.issue.payments' | transloco }}</h2>
          <ul>
            @for (pay of p.payments; track $index) {
              <li>{{ 'enums.paymentMethod.' + pay.method | transloco }} · {{ pay.amount | fcMoney }} · {{ pay.paidAt | fcDateTime }}</li>
            }
          </ul>
        </section>
      }

      @if (!p.existingInvoiceId) {
        <div class="inline-actions">
          <button type="button" class="btn btn-primary" [disabled]="busy() || !p.canIssue" (click)="issue(true)">{{ 'invoicing.issue.issueAndSend' | transloco }}</button>
          <button type="button" class="btn" [disabled]="busy() || !p.canIssue" (click)="issue(false)">{{ 'invoicing.issue.issueOnly' | transloco }}</button>
        </div>
        <p class="muted">{{ 'invoicing.issue.help' | transloco }}</p>
      }
    }
  `,
})
export class InvoiceIssuePage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  protected readonly types = BUYER_ID_TYPES;
  protected readonly preview = signal<SriInvoicePreview | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly buyerType = signal<SriBuyerIdType | null>(null);
  protected readonly issues = computed(() => (this.preview() ? splitIssues(this.preview()!) : { blocking: [], warnings: [] }));

  private obligationId = '';

  ngOnInit(): void {
    this.obligationId = this.route.snapshot.paramMap.get('obligationId') ?? '';
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.api.previewObligationInvoice(this.obligationId, this.buyerType()).subscribe({
      next: (p) => {
        this.loading.set(false);
        this.preview.set(p);
      },
      error: (err: unknown) => {
        this.loading.set(false);
        this.loadError.set(this.errors.message(err));
      },
    });
  }

  protected changeType(type: string): void {
    this.buyerType.set(type as SriBuyerIdType);
    this.load();
  }

  protected issue(submit: boolean): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.issueSriInvoice(this.obligationId, this.buyerType(), submit).subscribe({
      next: (invoice) => {
        this.busy.set(false);
        this.toast.success(submit ? 'invoicing.issue.issuedAndQueued' : 'invoicing.issue.issued');
        void this.router.navigate(['/invoicing/invoices', invoice.id]);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
