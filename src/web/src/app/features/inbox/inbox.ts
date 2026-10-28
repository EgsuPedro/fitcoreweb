import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { InboxNotification, NotificationPreference } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDateTime } from '../../shared/format.pipes';
import { ListState } from '../../shared/list-state';
import { Pager } from '../../shared/pager';
import { ToastService } from '../../shared/toast';
import { InboxService } from './inbox.service';

/** Bandeja de avisos del usuario (alumno, instructor o personal) y sus preferencias. Siempre es la del usuario de la sesión. */
@Component({
  selector: 'fc-inbox',
  imports: [TranslocoPipe, Badge, Pager, FcDateTime],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <h1>{{ 'inbox.title' | transloco }}</h1>
      <button type="button" class="btn" [disabled]="inbox.unread() === 0" (click)="markAll()">{{ 'inbox.markAll' | transloco }}</button>
    </header>

    <div class="toolbar">
      <select [attr.aria-label]="'inbox.filter' | transloco" (change)="setFilter($any($event.target).value)">
        <option value="">{{ 'inbox.all' | transloco }}</option>
        <option value="unread">{{ 'inbox.unreadOnly' | transloco }}</option>
      </select>
    </div>

    @if (list.error()) {
      <p class="alert alert-danger" role="alert">{{ list.error() }}</p>
    }

    <ul class="inbox-list" [attr.aria-label]="'inbox.title' | transloco">
      @for (n of list.items(); track n.id) {
        <li class="card inbox-item" [class.unread]="!n.isRead">
          <div class="inbox-head">
            <strong>{{ n.title }}</strong>
            @if (n.critical) {
              <fc-badge tone="warn">{{ 'inbox.critical' | transloco }}</fc-badge>
            }
            @if (!n.isRead) {
              <fc-badge tone="info">{{ 'inbox.new' | transloco }}</fc-badge>
            }
            <span class="muted inbox-time">{{ n.createdAt | fcDateTime }}</span>
          </div>
          <p>{{ n.body }}</p>
          @if (!n.isRead) {
            <button type="button" class="btn" (click)="markRead(n)">{{ 'inbox.markRead' | transloco }}</button>
          }
        </li>
      } @empty {
        <li class="empty">{{ (list.loading() ? 'common.loading' : 'inbox.empty') | transloco }}</li>
      }
    </ul>
    <fc-pager [page]="list.page()" [pageSize]="list.pageSize" [total]="list.total()" (pageChange)="list.goTo($event)" />

    <section class="card section">
      <h2>{{ 'inbox.preferences' | transloco }}</h2>
      <p class="muted">{{ 'inbox.preferencesHint' | transloco }}</p>
      @if (prefError()) {
        <p class="alert alert-danger" role="alert">{{ prefError() }}</p>
      }
      @for (p of preferences(); track p.type) {
        <label class="check pref" [class.disabled]="p.required">
          <input type="checkbox" [checked]="p.enabled" [disabled]="p.required || savingPref()" (change)="togglePreference(p, $any($event.target).checked)" />
          <span>
            <strong>{{ 'inbox.types.' + p.type + '.label' | transloco }}</strong>
            @if (p.required) {
              <small class="muted"> · {{ 'inbox.required' | transloco }}</small>
            }
            <br />
            <small class="muted">{{ 'inbox.types.' + p.type + '.description' | transloco }}</small>
          </span>
        </label>
      }
    </section>
  `,
})
export class Inbox implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  protected readonly inbox = inject(InboxService);

  protected readonly list = new ListState<InboxNotification>((query) => this.api.inbox(query), this.errors, inject(DestroyRef));
  protected readonly preferences = signal<NotificationPreference[]>([]);
  protected readonly prefError = signal<string | null>(null);
  protected readonly savingPref = signal(false);

  ngOnInit(): void {
    this.list.load();
    this.api.notificationPreferences().subscribe({
      next: (preferences) => this.preferences.set(preferences),
      error: (err: unknown) => this.prefError.set(this.errors.message(err)),
    });
  }

  protected setFilter(value: string): void {
    this.list.set({ unreadOnly: value === 'unread' });
  }

  protected markRead(notification: InboxNotification): void {
    this.api.markInboxRead(notification.id).subscribe({
      next: () => {
        this.list.load();
        this.inbox.refresh();
      },
      error: (err: unknown) => this.list.error.set(this.errors.message(err)),
    });
  }

  protected markAll(): void {
    this.api.markAllInboxRead().subscribe({
      next: () => {
        this.toast.success('inbox.allReadToast');
        this.list.load();
        this.inbox.refresh();
      },
      error: (err: unknown) => this.list.error.set(this.errors.message(err)),
    });
  }

  protected togglePreference(preference: NotificationPreference, enabled: boolean): void {
    this.savingPref.set(true);
    this.prefError.set(null);
    this.api.updateNotificationPreferences([{ type: preference.type, enabled }]).subscribe({
      next: (preferences) => {
        this.preferences.set(preferences);
        this.savingPref.set(false);
        this.toast.success('inbox.preferenceSavedToast');
      },
      error: (err: unknown) => {
        this.savingPref.set(false);
        this.prefError.set(this.errors.message(err));
        // El servidor manda: se vuelve a leer lo guardado para que la casilla no mienta.
        this.api.notificationPreferences().subscribe((preferences) => this.preferences.set(preferences));
      },
    });
  }
}
