import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { InboxService } from './inbox.service';

/** Campana de la barra superior: lleva a la bandeja y muestra cuántos avisos hay sin leer. */
@Component({
  selector: 'fc-inbox-bell',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (session.has(perm)) {
      <a
        class="btn btn-ghost bell"
        routerLink="/inbox"
        [attr.aria-label]="(inbox.unread() > 0 ? 'inbox.bellUnread' : 'inbox.bell') | transloco: { count: inbox.unread() }"
      >
        <span aria-hidden="true">🔔</span>
        @if (inbox.unread() > 0) {
          <span class="bell-count" aria-hidden="true">{{ inbox.unread() > 99 ? '99+' : inbox.unread() }}</span>
        }
      </a>
    }
  `,
})
export class InboxBell {
  protected readonly session = inject(SessionService);
  protected readonly inbox = inject(InboxService);
  protected readonly perm = Perm.InboxUse;
}
