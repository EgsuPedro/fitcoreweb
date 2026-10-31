import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Branch, EmissionPoint, InvoicingSettings } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { Confirm } from '../../shared/confirm';
import { Modal } from '../../shared/modal';
import { ToastService } from '../../shared/toast';

/** Aviso previo, no la validación: 13 dígitos terminados en 001 (el esquema del SRI solo admite RUC de emisor así). El servidor aplica además los módulos 10 y 11. */
export function looksLikeRuc(value: string): boolean {
  const ruc = value.replace(/[\s-]/g, '');
  return /^\d{10}001$/.test(ruc);
}

/**
 * Configuración de la facturación electrónica del gimnasio: datos tributarios del emisor, establecimientos y puntos de emisión, ambiente del SRI y certificado de firma.
 * El certificado y su contraseña se envían una sola vez, se guardan cifrados y nunca se vuelven a mostrar; aquí solo se ven sus datos públicos.
 */
@Component({
  selector: 'fc-invoicing-settings',
  imports: [ReactiveFormsModule, TranslocoPipe, DatePipe, Badge, Confirm, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'invoicing.title' | transloco }}</h1>
        <p class="muted">{{ 'invoicing.subtitle' | transloco }}</p>
      </div>
    </header>

    @if (loadError()) { <p class="alert alert-danger" role="alert">{{ loadError() }}</p> }
    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }
    @if (!canConfigure()) { <p class="alert alert-info">{{ 'invoicing.readOnly' | transloco }}</p> }

    @if (settings(); as s) {
      <section class="card section" [attr.aria-label]="'invoicing.status.title' | transloco">
        <h2>
          {{ 'invoicing.status.title' | transloco }}
          <fc-badge [tone]="s.canIssue ? 'ok' : 'danger'">{{ (s.canIssue ? 'invoicing.status.ready' : 'invoicing.status.notReady') | transloco }}</fc-badge>
        </h2>
        <ul class="checklist">
          @for (c of s.checks; track c.code) {
            <li [class.bad]="!c.ok && c.blocking" [class.warn]="!c.ok && !c.blocking">
              <span aria-hidden="true">{{ c.ok ? '✓' : c.blocking ? '✗' : '!' }}</span>
              <span>{{ 'invoicing.checks.' + c.code | transloco }}</span>
              <span class="sr-only">{{ (c.ok ? 'invoicing.status.ok' : c.blocking ? 'invoicing.status.blocking' : 'invoicing.status.warning') | transloco }}</span>
            </li>
          }
        </ul>
        @if (s.issuer?.environment === 'Test') { <p class="alert alert-warning">{{ 'invoicing.testEnvironment' | transloco }}</p> }
      </section>

      <form class="card section" [formGroup]="issuerForm" (ngSubmit)="saveIssuer()" novalidate>
        <h2>{{ 'invoicing.issuer.title' | transloco }}</h2>
        <fieldset [disabled]="!canConfigure()" class="plain">
          <div class="form-grid">
            <label class="field">
              <span>{{ 'invoicing.issuer.ruc' | transloco }}</span>
              <input formControlName="ruc" inputmode="numeric" maxlength="13" autocomplete="off" [attr.aria-invalid]="bad('ruc')" />
              @if (bad('ruc')) { <small class="field-error" role="alert">{{ 'invoicing.issuer.rucInvalid' | transloco }}</small> }
            </label>
            <label class="field">
              <span>{{ 'invoicing.issuer.legalName' | transloco }}</span>
              <input formControlName="legalName" maxlength="300" [attr.aria-invalid]="bad('legalName')" />
            </label>
            <label class="field">
              <span>{{ 'invoicing.issuer.tradeName' | transloco }}</span>
              <input formControlName="tradeName" maxlength="300" />
            </label>
            <label class="field">
              <span>{{ 'invoicing.issuer.headOffice' | transloco }}</span>
              <input formControlName="headOfficeAddress" maxlength="300" [attr.aria-invalid]="bad('headOfficeAddress')" />
            </label>
            <label class="field">
              <span>{{ 'invoicing.issuer.regime' | transloco }}</span>
              <select formControlName="taxRegime">
                @for (r of regimes; track r) { <option [value]="r">{{ 'invoicing.regimes.' + r | transloco }}</option> }
              </select>
              @if (issuerForm.controls.taxRegime.value === 'RimpeNegocioPopular') { <small class="field-error">{{ 'invoicing.issuer.popularWarning' | transloco }}</small> }
            </label>
            <label class="field">
              <span>{{ 'invoicing.issuer.notificationEmail' | transloco }}</span>
              <input formControlName="notificationEmail" type="email" maxlength="256" autocomplete="off" />
              <small class="muted">{{ 'invoicing.issuer.notificationHint' | transloco }}</small>
            </label>
            <label class="field">
              <span>{{ 'invoicing.issuer.special' | transloco }}</span>
              <input formControlName="specialTaxpayerNumber" maxlength="13" />
            </label>
            <label class="field">
              <span>{{ 'invoicing.issuer.withholding' | transloco }}</span>
              <input formControlName="withholdingAgentNumber" maxlength="13" />
            </label>
          </div>
          <label class="check"><input type="checkbox" formControlName="accountingRequired" /><span>{{ 'invoicing.issuer.accounting' | transloco }}</span></label>
          <label class="field">
            <span>{{ 'invoicing.issuer.card' | transloco }}</span>
            <select formControlName="cardPaymentMethod">
              <option value="Credit">{{ 'invoicing.cards.Credit' | transloco }}</option>
              <option value="Debit">{{ 'invoicing.cards.Debit' | transloco }}</option>
            </select>
            <small class="muted">{{ 'invoicing.issuer.cardHint' | transloco }}</small>
          </label>
          <label class="field">
            <span>{{ 'invoicing.issuer.environment' | transloco }}</span>
            <select formControlName="environment">
              <option value="Test">{{ 'invoicing.environments.Test' | transloco }}</option>
              <option value="Production">{{ 'invoicing.environments.Production' | transloco }}</option>
            </select>
          </label>
          @if (issuerForm.controls.environment.value === 'Production') {
            <label class="check">
              <input type="checkbox" formControlName="confirmProduction" />
              <span>{{ 'invoicing.issuer.confirmProduction' | transloco }}</span>
            </label>
          }
        </fieldset>
        @if (canConfigure()) {
          <div class="inline-actions"><button type="submit" class="btn btn-primary" [disabled]="busy()">{{ 'common.save' | transloco }}</button></div>
        }
      </form>

      <section class="card section">
        <div class="section-head">
          <h2>{{ 'invoicing.points.title' | transloco }}</h2>
          @if (canConfigure()) { <button type="button" class="btn" (click)="openPoint(null)">{{ 'invoicing.points.new' | transloco }}</button> }
        </div>
        <p class="muted">{{ 'invoicing.points.intro' | transloco }}</p>
        <div class="table-card">
          <table class="table">
            <caption class="sr-only">{{ 'invoicing.points.title' | transloco }}</caption>
            <thead>
              <tr>
                <th scope="col">{{ 'invoicing.points.establishment' | transloco }}</th>
                <th scope="col">{{ 'invoicing.points.point' | transloco }}</th>
                <th scope="col">{{ 'invoicing.points.address' | transloco }}</th>
                <th scope="col">{{ 'invoicing.points.branch' | transloco }}</th>
                <th scope="col">{{ 'invoicing.points.lastSequential' | transloco }}</th>
                <th scope="col">{{ 'common.status' | transloco }}</th>
                <th scope="col"></th>
              </tr>
            </thead>
            <tbody>
              @for (p of s.emissionPoints; track p.id) {
                <tr>
                  <th scope="row">{{ p.establishmentCode }}</th>
                  <td>{{ p.pointCode }}</td>
                  <td>{{ p.establishmentAddress }}</td>
                  <td>{{ p.branchName || '—' }}</td>
                  <td>{{ p.lastSequential }}</td>
                  <td><fc-badge [tone]="p.isActive ? 'ok' : 'off'">{{ (p.isActive ? 'common.active' : 'common.inactive') | transloco }}</fc-badge></td>
                  <td class="row-actions">
                    @if (canConfigure()) {
                      <button type="button" class="btn" (click)="openPoint(p)">{{ 'common.edit' | transloco }}</button>
                      <button type="button" class="btn" (click)="openSequence(p)">{{ 'invoicing.points.sequence' | transloco }}</button>
                      <button type="button" class="btn" [disabled]="busy()" (click)="togglePoint(p)">{{ (p.isActive ? 'invoicing.points.deactivate' : 'invoicing.points.activate') | transloco }}</button>
                    }
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="7" class="empty">{{ 'invoicing.points.empty' | transloco }}</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>

      <section class="card section">
        <h2>{{ 'invoicing.certificate.title' | transloco }}</h2>
        <p class="muted">{{ 'invoicing.certificate.intro' | transloco }}</p>
        @if (!s.masterKeyConfigured) { <p class="alert alert-danger" role="alert">{{ 'errors.invoicing.master_key_missing' | transloco }}</p> }
        @if (s.certificate; as c) {
          <dl class="summary">
            <dt>{{ 'invoicing.certificate.subject' | transloco }}</dt><dd>{{ c.subject }}</dd>
            <dt>{{ 'invoicing.certificate.issuer' | transloco }}</dt><dd>{{ c.issuer }}</dd>
            <dt>{{ 'invoicing.certificate.serial' | transloco }}</dt><dd><code>{{ c.serialNumber }}</code></dd>
            <dt>{{ 'invoicing.certificate.valid' | transloco }}</dt>
            <dd>
              {{ c.notBefore | date: 'mediumDate' }} → {{ c.notAfter | date: 'mediumDate' }}
              <fc-badge [tone]="c.expired ? 'danger' : c.daysToExpire <= 30 ? 'warn' : 'ok'">
                {{ (c.expired ? 'invoicing.certificate.expired' : 'invoicing.certificate.daysLeft') | transloco: { days: c.daysToExpire } }}
              </fc-badge>
            </dd>
            @if (c.matchesIssuerRuc === false) { <dt></dt><dd class="field-error">{{ 'invoicing.certificate.otherRuc' | transloco }}</dd> }
          </dl>
        } @else {
          <p class="alert alert-warning">{{ 'invoicing.certificate.none' | transloco }}</p>
        }

        @if (canConfigure()) {
          <form [formGroup]="certForm" (ngSubmit)="uploadCertificate()" novalidate>
            <div class="form-grid">
              <label class="field">
                <span>{{ 'invoicing.certificate.file' | transloco }}</span>
                <input type="file" accept=".p12,.pfx,application/x-pkcs12" (change)="pick($any($event.target).files)" />
              </label>
              <label class="field">
                <span>{{ 'invoicing.certificate.password' | transloco }}</span>
                <input formControlName="password" type="password" autocomplete="new-password" />
                <small class="muted">{{ 'invoicing.certificate.passwordHint' | transloco }}</small>
              </label>
            </div>
            <div class="inline-actions">
              <button type="submit" class="btn btn-primary" [disabled]="busy() || !file() || !s.masterKeyConfigured">
                {{ (s.certificate ? 'invoicing.certificate.replace' : 'invoicing.certificate.upload') | transloco }}
              </button>
              @if (s.certificate) {
                <button type="button" class="btn btn-danger" [disabled]="busy()" (click)="confirmRetire.set(true)">{{ 'invoicing.certificate.retire' | transloco }}</button>
              }
            </div>
          </form>
        }
      </section>
    }

    @if (pointEditor(); as editor) {
      <fc-modal [title]="(editor.id ? 'invoicing.points.edit' : 'invoicing.points.new') | transloco" (closed)="pointEditor.set(null)">
        <form id="point-form" [formGroup]="pointForm" (ngSubmit)="savePoint()" novalidate>
          @if (formError()) { <p class="alert alert-danger" role="alert">{{ formError() }}</p> }
          <div class="form-grid">
            <label class="field">
              <span>{{ 'invoicing.points.establishment' | transloco }}</span>
              <input formControlName="establishmentCode" inputmode="numeric" maxlength="3" placeholder="001" [readonly]="!!editor.id" />
              <small class="muted">{{ 'invoicing.points.codeHint' | transloco }}</small>
            </label>
            <label class="field">
              <span>{{ 'invoicing.points.point' | transloco }}</span>
              <input formControlName="pointCode" inputmode="numeric" maxlength="3" placeholder="001" [readonly]="!!editor.id" />
            </label>
          </div>
          <label class="field">
            <span>{{ 'invoicing.points.address' | transloco }}</span>
            <input formControlName="establishmentAddress" maxlength="300" />
          </label>
          <label class="field">
            <span>{{ 'invoicing.points.branch' | transloco }}</span>
            <select formControlName="branchId">
              <option value="">—</option>
              @for (b of branches(); track b.id) { <option [value]="b.id">{{ b.code }} — {{ b.name }}</option> }
            </select>
          </label>
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="pointEditor.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="point-form" class="btn btn-primary" [disabled]="busy()">{{ 'common.save' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (sequenceEditor(); as editor) {
      <fc-modal [title]="'invoicing.points.sequenceTitle' | transloco" (closed)="sequenceEditor.set(null)">
        <form id="sequence-form" [formGroup]="sequenceForm" (ngSubmit)="saveSequence()" novalidate>
          @if (formError()) { <p class="alert alert-danger" role="alert">{{ formError() }}</p> }
          <p class="muted">{{ 'invoicing.points.sequenceIntro' | transloco: { point: editor.label, current: editor.current } }}</p>
          <label class="field">
            <span>{{ 'invoicing.points.sequenceLabel' | transloco }}</span>
            <input formControlName="lastIssued" type="number" inputmode="numeric" min="0" max="999999998" step="1" />
            <small class="muted">{{ 'invoicing.points.sequenceHint' | transloco }}</small>
          </label>
        </form>
        <ng-container footer>
          <button type="button" class="btn" (click)="sequenceEditor.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="submit" form="sequence-form" class="btn btn-primary" [disabled]="busy()">{{ 'common.save' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (confirmRetire()) {
      <fc-confirm
        [title]="'invoicing.certificate.retire' | transloco"
        [message]="'invoicing.certificate.retireConfirm' | transloco"
        [confirmLabel]="'invoicing.certificate.retire' | transloco"
        [danger]="true"
        (confirmed)="retire()"
        (cancelled)="confirmRetire.set(false)"
      />
    }
  `,
})
export class InvoicingSettingsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  protected readonly regimes = ['General', 'RimpeEmprendedor', 'RimpeNegocioPopular'] as const;
  protected readonly settings = signal<InvoicingSettings | null>(null);
  protected readonly branches = signal<Branch[]>([]);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly file = signal<File | null>(null);
  protected readonly pointEditor = signal<{ id: string | null } | null>(null);
  protected readonly confirmRetire = signal(false);
  protected readonly sequenceEditor = signal<{ id: string; label: string; current: number } | null>(null);
  protected readonly canConfigure = computed(() => this.session.has(Perm.InvoicingConfigure));

  protected readonly issuerForm = this.fb.nonNullable.group({
    ruc: ['', [Validators.required, (c: { value: string }) => (looksLikeRuc(c.value ?? '') ? null : { ruc: true })]],
    legalName: ['', [Validators.required, Validators.maxLength(300)]],
    tradeName: [''],
    headOfficeAddress: ['', [Validators.required, Validators.maxLength(300)]],
    taxRegime: ['General'],
    accountingRequired: [false],
    specialTaxpayerNumber: [''],
    withholdingAgentNumber: [''],
    environment: ['Test'],
    notificationEmail: ['', [Validators.email, Validators.maxLength(256)]],
    cardPaymentMethod: ['Credit'],
    confirmProduction: [false],
  });

  protected readonly pointForm = this.fb.nonNullable.group({
    establishmentCode: ['', [Validators.required, Validators.pattern(/^(?!000)\d{3}$/)]],
    pointCode: ['', [Validators.required, Validators.pattern(/^(?!000)\d{3}$/)]],
    establishmentAddress: ['', [Validators.required, Validators.maxLength(300)]],
    branchId: [''],
  });

  protected readonly sequenceForm = this.fb.nonNullable.group({
    lastIssued: [0, [Validators.required, Validators.min(0), Validators.max(999_999_998)]],
  });

  protected readonly certForm = this.fb.nonNullable.group({ password: ['', [Validators.required]] });

  ngOnInit(): void {
    this.api.invoicingSettings().subscribe({
      next: (s) => this.apply(s),
      error: (err: unknown) => this.loadError.set(this.errors.message(err)),
    });
    this.api.branches({ pageSize: 100 }).subscribe({ next: (r) => this.branches.set(r.items), error: () => undefined });
  }

  protected bad(name: 'ruc' | 'legalName' | 'headOfficeAddress'): boolean {
    const control = this.issuerForm.controls[name];
    return control.invalid && (control.touched || control.dirty);
  }

  protected saveIssuer(): void {
    if (this.issuerForm.invalid) {
      this.issuerForm.markAllAsTouched();
      this.error.set(this.errors.text('invoicing.invalidForm'));
      return;
    }
    const v = this.issuerForm.getRawValue();
    this.run(
      this.api.saveInvoicingIssuer({
        ruc: v.ruc.replace(/[\s-]/g, ''),
        legalName: v.legalName,
        tradeName: v.tradeName || null,
        headOfficeAddress: v.headOfficeAddress,
        taxRegime: v.taxRegime as 'General' | 'RimpeEmprendedor' | 'RimpeNegocioPopular',
        accountingRequired: v.accountingRequired,
        specialTaxpayerNumber: v.specialTaxpayerNumber || null,
        withholdingAgentNumber: v.withholdingAgentNumber || null,
        environment: v.environment as 'Test' | 'Production',
        notificationEmail: v.notificationEmail || null,
        cardPaymentMethod: v.cardPaymentMethod as 'Credit' | 'Debit',
        confirmProduction: v.confirmProduction,
      }),
      'invoicing.saved',
    );
  }

  protected openPoint(point: EmissionPoint | null): void {
    this.formError.set(null);
    this.pointForm.reset({
      establishmentCode: point?.establishmentCode ?? '',
      pointCode: point?.pointCode ?? '',
      establishmentAddress: point?.establishmentAddress ?? this.settings()?.issuer?.headOfficeAddress ?? '',
      branchId: point?.branchId ?? '',
    });
    this.pointEditor.set({ id: point?.id ?? null });
  }

  protected savePoint(): void {
    const editor = this.pointEditor();
    if (!editor) {
      return;
    }
    if (this.pointForm.invalid) {
      this.pointForm.markAllAsTouched();
      this.formError.set(this.errors.text('invoicing.invalidPoint'));
      return;
    }
    const v = this.pointForm.getRawValue();
    this.busy.set(true);
    this.formError.set(null);
    const call = editor.id
      ? this.api.updateEmissionPoint(editor.id, { branchId: v.branchId || null, establishmentAddress: v.establishmentAddress })
      : this.api.createEmissionPoint({ branchId: v.branchId || null, establishmentCode: v.establishmentCode, pointCode: v.pointCode, establishmentAddress: v.establishmentAddress });
    call.subscribe({
      next: (s) => {
        this.busy.set(false);
        this.pointEditor.set(null);
        this.apply(s);
        this.toast.success('invoicing.saved');
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  protected openSequence(point: EmissionPoint): void {
    this.formError.set(null);
    this.sequenceForm.reset({ lastIssued: point.lastSequential });
    this.sequenceEditor.set({ id: point.id, label: `${point.establishmentCode}-${point.pointCode}`, current: point.lastSequential });
  }

  protected saveSequence(): void {
    const editor = this.sequenceEditor();
    if (!editor) {
      return;
    }
    if (this.sequenceForm.invalid) {
      this.sequenceForm.markAllAsTouched();
      this.formError.set(this.errors.text('invoicing.points.sequenceInvalid'));
      return;
    }
    this.busy.set(true);
    this.formError.set(null);
    this.api.setEmissionPointSequence(editor.id, Math.trunc(this.sequenceForm.getRawValue().lastIssued)).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.sequenceEditor.set(null);
        this.apply(s);
        this.toast.success('invoicing.saved');
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  protected togglePoint(point: EmissionPoint): void {
    this.run(this.api.setEmissionPointStatus(point.id, !point.isActive), 'invoicing.saved');
  }

  protected pick(files: FileList | null): void {
    this.file.set(files?.item(0) ?? null);
  }

  protected uploadCertificate(): void {
    const file = this.file();
    if (!file || this.certForm.invalid) {
      this.certForm.markAllAsTouched();
      return;
    }
    const password = this.certForm.getRawValue().password;
    this.run(this.api.uploadSigningCertificate(file, password), 'invoicing.certificate.saved', () => {
      // La contraseña no se conserva en la pantalla un instante más de lo necesario.
      this.certForm.reset({ password: '' });
      this.file.set(null);
    });
  }

  protected retire(): void {
    this.confirmRetire.set(false);
    this.run(this.api.retireSigningCertificate(), 'invoicing.certificate.retired');
  }

  private run(call: ReturnType<ApiService['invoicingSettings']>, toastKey: string, after?: () => void): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: (s) => {
        this.busy.set(false);
        this.apply(s);
        after?.();
        this.toast.success(toastKey);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
        this.certForm.reset({ password: '' });
      },
    });
  }

  private apply(s: InvoicingSettings): void {
    this.settings.set(s);
    const i = s.issuer;
    this.issuerForm.reset({
      ruc: i?.ruc ?? '',
      legalName: i?.legalName ?? '',
      tradeName: i?.tradeName ?? '',
      headOfficeAddress: i?.headOfficeAddress ?? '',
      taxRegime: i?.taxRegime ?? 'General',
      accountingRequired: i?.accountingRequired ?? false,
      specialTaxpayerNumber: i?.specialTaxpayerNumber ?? '',
      withholdingAgentNumber: i?.withholdingAgentNumber ?? '',
      environment: i?.environment ?? 'Test',
      notificationEmail: i?.notificationEmail ?? '',
      cardPaymentMethod: i?.cardPaymentMethod ?? 'Credit',
      confirmProduction: false,
    });
  }
}
