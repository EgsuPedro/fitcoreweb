import { ChangeDetectionStrategy, Component, OnInit, inject, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import { BrandingService } from '../../core/branding.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Confirm } from '../../shared/confirm';
import { LogoPicker } from '../../shared/logo-picker';
import { ToastService } from '../../shared/toast';

/**
 * Logo del gimnasio. Aparece en el menú y en los libros de Excel de los reportes (y, más adelante, en los comprobantes). Solo PNG o JPG de hasta 1 MB;
 * el servidor valida el archivo, le quita los metadatos y lo guarda en el almacén privado del gimnasio.
 */
@Component({
  selector: 'fc-branding',
  imports: [TranslocoPipe, LogoPicker, Confirm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ 'branding.title' | transloco }}</h1>
        <p class="muted">{{ 'branding.subtitle' | transloco }}</p>
      </div>
    </header>

    @if (error()) { <p class="alert alert-danger" role="alert">{{ error() }}</p> }

    <section class="card">
      <h2>{{ brand.branding()?.tenantName }}</h2>
      <fc-logo-picker [currentUrl]="brand.logoUrl()" [disabled]="busy()" (fileChange)="file.set($event)" />
      <div class="row-actions">
        <button type="button" class="btn btn-primary" [disabled]="busy() || !file()" (click)="save()">{{ 'branding.save' | transloco }}</button>
        @if (brand.branding()?.hasLogo) {
          <button type="button" class="btn btn-danger" [disabled]="busy()" (click)="confirmRemove.set(true)">{{ 'branding.remove' | transloco }}</button>
        }
      </div>
      <p class="muted">{{ 'branding.usedIn' | transloco }}</p>
    </section>

    @if (confirmRemove()) {
      <fc-confirm
        [title]="'branding.remove' | transloco"
        [message]="'branding.removeConfirm' | transloco"
        [confirmLabel]="'branding.remove' | transloco"
        [danger]="true"
        (confirmed)="remove()"
        (cancelled)="confirmRemove.set(false)"
      />
    }
  `,
})
export class BrandingPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  protected readonly brand = inject(BrandingService);

  private readonly picker = viewChild(LogoPicker);
  protected readonly file = signal<File | null>(null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly confirmRemove = signal(false);

  ngOnInit(): void {
    this.brand.refresh();
  }

  protected save(): void {
    const file = this.file();
    if (!file) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.api.uploadLogo(file).subscribe({
      next: () => {
        this.busy.set(false);
        this.file.set(null);
        this.picker()?.reset();
        this.toast.success('branding.saved');
        this.brand.refresh();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  protected remove(): void {
    this.confirmRemove.set(false);
    this.busy.set(true);
    this.error.set(null);
    this.api.removeLogo().subscribe({
      next: () => {
        this.busy.set(false);
        this.toast.success('branding.removed');
        this.brand.refresh();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }
}
