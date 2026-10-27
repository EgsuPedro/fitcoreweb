import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { EMPTY, Subject, catchError, debounceTime, switchMap } from 'rxjs';
import { ApiService } from '../core/api/api.service';
import type { Member } from '../core/api/models';
import { ErrorMessages } from '../core/i18n/messages';

/**
 * Selector de alumno con búsqueda por nombre, código, documento o teléfono. Busca en el servidor (nunca carga todos los
 * alumnos) y respeta el alcance por sucursal del usuario.
 */
@Component({
  selector: 'fc-member-picker',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (selected(); as member) {
      <div class="picked">
        <span>
          <strong>{{ member.firstName }} {{ member.lastName }}</strong>
          <span class="muted"> · {{ member.code }} · {{ member.idNumber }}</span>
        </span>
        @if (!locked()) {
          <button type="button" class="btn btn-ghost" (click)="clear()">{{ 'common.change' | transloco }}</button>
        }
      </div>
    } @else {
      <input
        type="search"
        class="search"
        [placeholder]="'members.searchHint' | transloco"
        [attr.aria-label]="'members.searchHint' | transloco"
        (input)="onInput($any($event.target).value)"
      />
      @if (error()) {
        <p class="alert alert-danger" role="alert">{{ error() }}</p>
      }
      @if (results().length > 0) {
        <ul class="picker-results" role="listbox">
          @for (member of results(); track member.id) {
            <li>
              <button type="button" role="option" class="picker-option" (click)="pick(member)">
                <strong>{{ member.firstName }} {{ member.lastName }}</strong>
                <span class="muted">{{ member.code }} · {{ member.idNumber }}</span>
              </button>
            </li>
          }
        </ul>
      } @else if (searched()) {
        <p class="muted">{{ 'members.noResults' | transloco }}</p>
      }
    }
  `,
})
export class MemberPicker {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);

  /** Alumno ya elegido (por ejemplo, al abrir el diálogo desde su ficha). */
  readonly initial = input<Member | null>(null);
  readonly locked = input(false);
  readonly picked = output<Member | null>();

  protected readonly selected = signal<Member | null>(null);
  protected readonly results = signal<Member[]>([]);
  protected readonly searched = signal(false);
  protected readonly error = signal<string | null>(null);

  private readonly text = new Subject<string>();

  constructor() {
    const destroyRef = inject(DestroyRef);
    queueMicrotask(() => this.selected.set(this.initial()));
    this.text
      .pipe(
        debounceTime(300),
        switchMap((search) => {
          if (search.trim().length < 2) {
            this.results.set([]);
            this.searched.set(false);
            return EMPTY;
          }
          this.error.set(null);
          return this.api.members({ search: search.trim(), pageSize: 8 }).pipe(
            catchError((err: unknown) => {
              this.error.set(this.errors.message(err));
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(destroyRef),
      )
      .subscribe((page) => {
        this.results.set(page.items);
        this.searched.set(true);
      });
  }

  protected onInput(value: string): void {
    this.text.next(value);
  }

  protected pick(member: Member): void {
    this.selected.set(member);
    this.results.set([]);
    this.picked.emit(member);
  }

  protected clear(): void {
    this.selected.set(null);
    this.searched.set(false);
    this.picked.emit(null);
  }
}
