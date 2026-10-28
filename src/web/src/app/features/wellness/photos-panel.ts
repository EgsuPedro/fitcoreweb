import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { PhotoConsent, ProgressPhoto } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { AuthImage } from '../../shared/auth-image';
import { Badge } from '../../shared/badge';
import { Confirm } from '../../shared/confirm';
import { FcDate, FcDateTime } from '../../shared/format.pipes';
import { todayIso } from '../../shared/form-utils';
import { ToastService } from '../../shared/toast';

const MAX_BYTES = 8 * 1024 * 1024;

/**
 * Fotos de progreso. Son datos personales sensibles: solo se guardan con consentimiento registrado, viven en almacenamiento
 * privado y se piden a la API con la sesión. Retirar el consentimiento borra todas las fotos.
 */
@Component({
  selector: 'fc-photos-panel',
  imports: [TranslocoPipe, Badge, FcDate, FcDateTime, AuthImage, Confirm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    @if (consent(); as c) {
      <div class="subcard">
        <h3>
          {{ 'photos.consentTitle' | transloco }}
          @if (c.status === 'Granted') {
            <fc-badge tone="ok">{{ 'photos.granted' | transloco }}</fc-badge>
          } @else if (c.status === 'Revoked') {
            <fc-badge tone="neutral">{{ 'photos.revoked' | transloco }}</fc-badge>
          } @else {
            <fc-badge tone="warn">{{ 'photos.none' | transloco }}</fc-badge>
          }
        </h3>

        @if (c.status === 'Granted') {
          <p class="muted">
            {{ 'photos.grantedSince' | transloco: { date: '' } }}{{ c.since | fcDateTime }} ·
            {{ 'enums.photoConsentSource.' + c.source | transloco }}@if (c.guardianName) { · {{ 'photos.guardian' | transloco }}: {{ c.guardianName }} }
          </p>
          @if (canManage()) {
            <div class="inline-actions">
              <button type="button" class="btn btn-danger" (click)="confirmingRevoke.set(true)">{{ 'photos.revoke' | transloco }}</button>
            </div>
          }
        } @else if (canManage()) {
          <div class="consent-text">
            <p>{{ 'photos.notice' | transloco }}</p>
            <ul>
              <li>{{ 'photos.notice1' | transloco }}</li>
              <li>{{ 'photos.notice2' | transloco }}</li>
              <li>{{ 'photos.notice3' | transloco }}</li>
            </ul>
          </div>
          @if (!self()) {
            <label class="field">
              <span>{{ 'photos.guardianName' | transloco }}</span>
              <input maxlength="120" [value]="guardian()" (input)="guardian.set($any($event.target).value)" />
              <small class="muted">{{ 'photos.guardianHint' | transloco }}</small>
            </label>
          }
          <label class="check">
            <input type="checkbox" [checked]="accepted()" (change)="accepted.set($any($event.target).checked)" />
            <span>{{ (self() ? 'photos.acceptSelf' : 'photos.acceptStaff') | transloco }}</span>
          </label>
          <div class="inline-actions">
            <button type="button" class="btn btn-primary" [disabled]="!accepted() || busy()" (click)="grant()">{{ 'photos.grant' | transloco }}</button>
          </div>
        }
      </div>

      @if (c.status === 'Granted') {
        @if (canManage()) {
          <div class="subcard">
            <h3>{{ 'photos.upload' | transloco }}</h3>
            <div class="form-grid">
              <label class="field">
                <span>{{ 'photos.file' | transloco }}</span>
                <input type="file" accept="image/jpeg,image/png" (change)="pick($event)" />
                <small class="muted">{{ 'photos.fileHint' | transloco }}</small>
              </label>
              <label class="field"><span>{{ 'photos.takenOn' | transloco }}</span><input type="date" [value]="takenOn()" (input)="takenOn.set($any($event.target).value)" /></label>
              <label class="field">
                <span>{{ 'photos.angle' | transloco }}</span>
                <select [value]="angle()" (change)="angle.set($any($event.target).value)">
                  @for (a of angles; track a) {
                    <option [value]="a">{{ 'enums.photoAngle.' + a | transloco }}</option>
                  }
                </select>
              </label>
            </div>
            <div class="inline-actions">
              <button type="button" class="btn btn-primary" [disabled]="!file() || busy()" (click)="upload()">{{ 'photos.uploadButton' | transloco }}</button>
            </div>
          </div>
        }

        @if (photos().length >= 2) {
          <p class="muted"><small>{{ 'photos.compareHint' | transloco }}</small></p>
        }
        <div class="photo-grid">
          @for (p of photos(); track p.id) {
            <figure>
              <fc-auth-image [photoId]="p.id" [alt]="('enums.photoAngle.' + p.angle | transloco) + ' · ' + p.takenOn" />
              <figcaption>
                {{ p.takenOn | fcDate }} · {{ 'enums.photoAngle.' + p.angle | transloco }}
                <span class="inline-actions">
                  <button type="button" class="btn btn-ghost" [class.active]="compare().includes(p.id)" [attr.aria-pressed]="compare().includes(p.id)" (click)="toggle(p.id)">
                    {{ 'photos.compare' | transloco }}
                  </button>
                  @if (canManage()) {
                    <button type="button" class="btn btn-ghost" (click)="deleting.set(p)">{{ 'common.delete' | transloco }}</button>
                  }
                </span>
              </figcaption>
            </figure>
          } @empty {
            <p class="muted">{{ 'photos.empty' | transloco }}</p>
          }
        </div>

        @if (compared(); as pair) {
          <div class="subcard">
            <h3>{{ 'photos.comparison' | transloco }}</h3>
            <div class="photo-compare">
              @for (p of pair; track p.id) {
                <figure>
                  <fc-auth-image [photoId]="p.id" [alt]="('enums.photoAngle.' + p.angle | transloco) + ' · ' + p.takenOn" />
                  <figcaption>{{ p.takenOn | fcDate }} · {{ 'enums.photoAngle.' + p.angle | transloco }}</figcaption>
                </figure>
              }
            </div>
          </div>
        }
      }

      @if (c.history.length > 0) {
        <details>
          <summary>{{ 'photos.history' | transloco }}</summary>
          <ul class="timeline">
            @for (h of c.history; track h.id) {
              <li>
                <strong>{{ 'enums.photoConsentAction.' + h.action | transloco }}</strong>
                <span class="muted"> · {{ h.recordedAt | fcDateTime }} · {{ 'enums.photoConsentSource.' + h.source | transloco }} · {{ 'photos.noticeVersion' | transloco: { version: h.noticeVersion } }}</span>
              </li>
            }
          </ul>
        </details>
      }
    }

    @if (confirmingRevoke()) {
      <fc-confirm
        [title]="'photos.revokeTitle' | transloco"
        [message]="'photos.revokeMessage' | transloco: { count: consent()?.photoCount ?? 0 }"
        [confirmLabel]="'photos.revokeConfirm' | transloco"
        [danger]="true"
        (confirmed)="revoke()"
        (cancelled)="confirmingRevoke.set(false)"
      />
    }
    @if (deleting(); as p) {
      <fc-confirm
        [title]="'photos.deleteTitle' | transloco"
        [message]="'photos.deleteMessage' | transloco"
        [confirmLabel]="'common.delete' | transloco"
        [danger]="true"
        (confirmed)="remove(p)"
        (cancelled)="deleting.set(null)"
      />
    }
  `,
})
export class PhotosPanel implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  readonly memberId = input.required<string>();
  /** El propio alumno (la pantalla del portal): él mismo da y retira su consentimiento. */
  readonly self = input(false);

  protected readonly angles = ['Front', 'Side', 'Back', 'Other'] as const;
  protected readonly consent = signal<PhotoConsent | null>(null);
  protected readonly photos = signal<ProgressPhoto[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly accepted = signal(false);
  protected readonly guardian = signal('');
  protected readonly file = signal<File | null>(null);
  protected readonly takenOn = signal(todayIso());
  protected readonly angle = signal<'Front' | 'Side' | 'Back' | 'Other'>('Front');
  protected readonly confirmingRevoke = signal(false);
  protected readonly deleting = signal<ProgressPhoto | null>(null);
  protected readonly compare = signal<string[]>([]);
  protected readonly canManage = computed(() => this.session.has(Perm.PhotosManage));
  protected readonly compared = computed(() => {
    const ids = this.compare();
    const pair = ids.map((id) => this.photos().find((p) => p.id === id)).filter((p): p is ProgressPhoto => !!p);
    return pair.length === 2 ? pair : null;
  });

  ngOnInit(): void {
    this.reload();
  }

  protected pick(event: Event): void {
    const chosen = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.error.set(null);
    if (chosen && chosen.size > MAX_BYTES) {
      this.file.set(null);
      this.error.set(this.errors.text('errors.photos.file_too_large'));
      return;
    }
    this.file.set(chosen);
  }

  protected toggle(id: string): void {
    this.compare.update((list) => (list.includes(id) ? list.filter((x) => x !== id) : list.length < 2 ? [...list, id] : [list[1], id]));
  }

  protected grant(): void {
    this.run(this.api.grantPhotoConsent(this.memberId(), this.self() ? null : this.guardian().trim() || null), 'photos.grantedToast', (c) => {
      this.consent.set(c);
      this.accepted.set(false);
    });
  }

  protected revoke(): void {
    this.confirmingRevoke.set(false);
    this.run(this.api.revokePhotoConsent(this.memberId()), 'photos.revokedToast', (r) => {
      this.consent.set(r.consent);
      this.photos.set([]);
      this.compare.set([]);
    });
  }

  protected upload(): void {
    const chosen = this.file();
    if (!chosen) {
      return;
    }
    this.run(this.api.uploadProgressPhoto(this.memberId(), chosen, this.takenOn(), this.angle()), 'photos.uploadedToast', () => {
      this.file.set(null);
      this.reload();
    });
  }

  protected remove(photo: ProgressPhoto): void {
    this.deleting.set(null);
    this.run(this.api.deleteProgressPhoto(photo.id), 'photos.deletedToast', () => {
      this.compare.update((list) => list.filter((id) => id !== photo.id));
      this.reload();
    });
  }

  private run<T>(call: import('rxjs').Observable<T>, toastKey: string, done: (value: T) => void): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: (value) => {
        this.busy.set(false);
        this.toast.success(toastKey);
        done(value);
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.error.set(this.errors.message(err));
      },
    });
  }

  private reload(): void {
    this.api.photoConsent(this.memberId()).subscribe({
      next: (c) => {
        this.consent.set(c);
        if (c.status === 'Granted') {
          this.api.progressPhotos(this.memberId()).subscribe({
            next: (list) => this.photos.set(list),
            error: (err: unknown) => this.error.set(this.errors.message(err)),
          });
        } else {
          this.photos.set([]);
        }
      },
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }
}
