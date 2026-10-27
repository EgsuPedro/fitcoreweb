import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { TrainerAssignment, TrainerNote } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { FcDate, FcDateTime } from '../../shared/format.pipes';
import { ToastService } from '../../shared/toast';

/**
 * Entrenador del alumno en su ficha: historial de asignaciones (quien administra entrenadores) y observaciones profesionales
 * (solo el entrenador asignado: el servidor responde 403 a cualquier otro, y aquí se explica sin mostrar nada).
 */
@Component({
  selector: 'fc-member-trainer-panel',
  imports: [RouterLink, TranslocoPipe, Badge, FcDate, FcDateTime],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (canManage()) {
      <section class="card section">
        <h2>{{ 'trainers.memberSection' | transloco }}</h2>
        @if (historyError()) {
          <p class="alert alert-danger" role="alert">{{ historyError() }}</p>
        }
        <table class="table">
          <tbody>
            @for (a of history(); track a.id) {
              <tr>
                <td><a [routerLink]="['/trainers', a.trainerId]">{{ a.trainerName }}</a></td>
                <td>{{ a.assignedOn | fcDate }} – {{ a.endedOn ? (a.endedOn | fcDate) : ('trainers.present' | transloco) }}</td>
                <td>
                  @if (a.isActive) {
                    <fc-badge tone="ok">{{ 'trainers.active' | transloco }}</fc-badge>
                  } @else {
                    <small class="muted">{{ a.endReason }}</small>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="3" class="empty">{{ 'trainers.noTrainer' | transloco }}</td></tr>
            }
          </tbody>
        </table>
      </section>
    }

    @if (canNotes()) {
      <section class="card section">
        <h2>{{ 'trainers.notes' | transloco }}</h2>
        @if (notesDenied()) {
          <p class="muted">{{ 'trainers.notesDenied' | transloco }}</p>
        } @else {
          @if (notesError()) {
            <p class="alert alert-danger" role="alert">{{ notesError() }}</p>
          }
          <label class="field">
            <span>{{ 'trainers.newNote' | transloco }}</span>
            <textarea [value]="draft()" maxlength="2000" (input)="draft.set($any($event.target).value)"></textarea>
            <small class="muted">{{ 'trainers.noteHint' | transloco }}</small>
          </label>
          <div class="inline-actions">
            <button type="button" class="btn btn-primary" [disabled]="saving() || draft().trim().length === 0" (click)="add()">
              {{ 'trainers.addNote' | transloco }}
            </button>
          </div>
          <ul class="timeline">
            @for (n of notes(); track n.id) {
              <li>
                <strong>{{ n.trainerName }}</strong><span class="muted"> · {{ n.createdAt | fcDateTime }}</span>
                <div class="note-text">{{ n.text }}</div>
              </li>
            } @empty {
              <li class="muted">{{ 'trainers.noNotes' | transloco }}</li>
            }
          </ul>
        }
      </section>
    }
  `,
})
export class MemberTrainerPanel implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  readonly memberId = input.required<string>();

  protected readonly history = signal<TrainerAssignment[]>([]);
  protected readonly notes = signal<TrainerNote[]>([]);
  protected readonly historyError = signal<string | null>(null);
  protected readonly notesError = signal<string | null>(null);
  protected readonly notesDenied = signal(false);
  protected readonly draft = signal('');
  protected readonly saving = signal(false);

  protected readonly canManage = computed(() => this.session.has(Perm.TrainersManage));
  protected readonly canNotes = computed(() => this.session.has(Perm.TrainersNotes));

  ngOnInit(): void {
    if (this.canManage()) {
      this.api.memberTrainerAssignments(this.memberId()).subscribe({
        next: (list) => this.history.set(list),
        error: (err: unknown) => this.historyError.set(this.errors.message(err)),
      });
    }
    if (this.canNotes()) {
      this.loadNotes();
    }
  }

  protected add(): void {
    const text = this.draft().trim();
    if (!text || this.saving()) {
      return;
    }
    this.saving.set(true);
    this.notesError.set(null);
    this.api.createTrainerNote(this.memberId(), text).subscribe({
      next: () => {
        this.saving.set(false);
        this.draft.set('');
        this.toast.success('trainers.noteSavedToast');
        this.loadNotes();
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.handleError(err);
      },
    });
  }

  private loadNotes(): void {
    this.api.trainerNotes(this.memberId(), { pageSize: 50 }).subscribe({
      next: (page) => {
        this.notesDenied.set(false);
        this.notes.set(page.items);
      },
      error: (err: unknown) => this.handleError(err),
    });
  }

  private handleError(err: unknown): void {
    // «No asignado» no es un fallo: es la regla de privacidad, y no se muestra nada del contenido.
    if (this.errors.problemOf(err)?.code === 'trainers.not_assigned' || this.errors.problemOf(err)?.code === 'trainers.no_profile') {
      this.notesDenied.set(true);
      return;
    }
    this.notesError.set(this.errors.message(err));
  }
}
