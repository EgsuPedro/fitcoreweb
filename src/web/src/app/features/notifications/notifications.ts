import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Notification, NotificationSettings } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge, Tone } from '../../shared/badge';
import { FcDate, FcDateTime } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';

const statusTone = (status: string): Tone =>
  status === 'Sent' ? 'ok' : status === 'Failed' ? 'danger' : status === 'Sending' ? 'info' : 'warn';

/** Avisos de WhatsApp por vencimiento de membresía: configuración del gimnasio e historial de lo enviado. */
@Component({
  selector: 'fc-notifications',
  imports: [ReactiveFormsModule, RouterLink, TranslocoPipe, Badge, Pager, FcDate, FcDateTime],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'notifications.title' | transloco }}</h1>
    </header>
    <p class="muted">{{ 'notifications.intro' | transloco }}</p>

    @if (settings(); as s) {
      @if (s.provider === 'Simulated') {
        <p class="alert alert-warning" role="status">{{ 'notifications.simulated' | transloco }}</p>
      }

      <form class="card section" [formGroup]="form" (ngSubmit)="save()" novalidate>
        <h2>{{ 'notifications.settings' | transloco }}</h2>
        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <fieldset [disabled]="!canManage()" class="field">
          <label class="check">
            <input type="checkbox" formControlName="enabled" />
            <span>
              <strong>{{ 'notifications.enabled' | transloco }}</strong><br />
              <small class="muted">{{ 'notifications.enabledHint' | transloco }}</small>
            </span>
          </label>
        </fieldset>
        <label class="field">
          <span>{{ 'notifications.staffPhones' | transloco }}</span>
          <textarea formControlName="staffPhones" rows="3" [attr.readonly]="canManage() ? null : ''" placeholder="0991234567"></textarea>
          <small class="muted">{{ 'notifications.staffPhonesHint' | transloco }}</small>
        </label>
        @if (canManage()) {
          <div class="inline-actions">
            <button type="submit" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
          </div>
        } @else {
          <p class="muted">{{ 'settings.readOnly' | transloco }}</p>
        }
      </form>
    } @else if (loadError()) {
      <p class="alert alert-danger" role="alert">{{ loadError() }}</p>
    }

    <section class="section">
      <h2>{{ 'notifications.history' | transloco }}</h2>
      <div class="toolbar">
        <select [attr.aria-label]="'common.status' | transloco" (change)="list.set({ status: $any($event.target).value })">
          <option value="">{{ 'common.all' | transloco }}</option>
          @for (status of statuses; track status) {
            <option [value]="status">{{ 'enums.notificationStatus.' + status | transloco }}</option>
          }
        </select>
        <select [attr.aria-label]="'notifications.type' | transloco" (change)="list.set({ type: $any($event.target).value })">
          <option value="">{{ 'common.all' | transloco }}</option>
          @for (type of types; track type) {
            <option [value]="type">{{ 'enums.notificationType.' + type | transloco }}</option>
          }
        </select>
      </div>

      @if (list.error()) {
        <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
      }

      <div class="card table-card">
        <table class="table">
          <thead>
            <tr>
              <th>{{ 'common.date' | transloco }}</th>
              <th>{{ 'notifications.member' | transloco }}</th>
              <th>{{ 'notifications.type' | transloco }}</th>
              <th>{{ 'notifications.recipient' | transloco }}</th>
              <th>{{ 'common.status' | transloco }}</th>
              <th>{{ 'notifications.message' | transloco }}</th>
            </tr>
          </thead>
          <tbody>
            @for (n of list.items(); track n.id) {
              <tr>
                <td>{{ n.createdAt | fcDateTime }}</td>
                <td>
                  <a [routerLink]="['/members', n.memberId]">{{ n.memberName }}</a>
                  <br /><small class="muted">{{ 'notifications.endsOn' | transloco }} {{ n.membershipEndDate | fcDate }}</small>
                </td>
                <td>{{ 'enums.notificationType.' + n.type | transloco }}</td>
                <td>{{ 'enums.notificationRecipientKind.' + n.recipientKind | transloco }}<br /><small class="muted">{{ n.recipient }}</small></td>
                <td>
                  <fc-badge [tone]="tone(n.status)">{{ 'enums.notificationStatus.' + n.status | transloco }}</fc-badge>
                  @if (n.provider === 'simulated') {
                    <br /><small class="muted">{{ 'notifications.simulatedTag' | transloco }}</small>
                  }
                  @if (n.lastError) {
                    <br /><small class="muted">{{ n.lastError }}</small>
                  }
                </td>
                <td class="message">{{ n.messageText }}</td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="empty">{{ (list.loading() ? 'common.loading' : 'notifications.empty') | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </div>
      <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />
    </section>
  `,
})
export class NotificationsPage implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);

  protected readonly list = new ListState<Notification>((query) => this.api.notifications(query), this.errors, inject(DestroyRef));
  protected readonly statuses = ['Pending', 'Sending', 'Sent', 'Failed'];
  protected readonly types = ['StaffExpiringSoon', 'MemberDueDay', 'MemberAfterDue'];
  protected readonly tone = statusTone;
  protected readonly canManage = computed(() => this.session.has(Perm.CommercialSettingsManage));

  protected readonly settings = signal<NotificationSettings | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly saving = signal(false);

  protected readonly form = this.fb.nonNullable.group({ enabled: [false], staffPhones: [''] });

  ngOnInit(): void {
    this.list.load();
    this.api.notificationSettings().subscribe({
      next: (settings) => this.apply(settings),
      error: (err: unknown) => this.loadError.set(this.errors.message(err)),
    });
  }

  protected save(): void {
    const phones = this.form.controls.staffPhones.value
      .split(/[\n,;]+/)
      .map((phone) => phone.trim())
      .filter((phone) => phone.length > 0);
    this.saving.set(true);
    this.formError.set(null);
    this.api.updateNotificationSettings({ enabled: this.form.controls.enabled.value, staffPhones: phones }).subscribe({
      next: (settings) => {
        this.saving.set(false);
        this.apply(settings);
        this.toast.success('notifications.saved');
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  private apply(settings: NotificationSettings): void {
    this.settings.set(settings);
    this.form.reset({ enabled: settings.enabled, staffPhones: settings.staffPhones.join('\n') });
  }
}
