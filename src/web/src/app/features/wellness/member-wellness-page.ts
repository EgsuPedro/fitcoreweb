import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { map } from 'rxjs';
import { MemberWellness } from './member-wellness';

/**
 * Seguimiento de un alumno para quien no administra alumnos (el entrenador no tiene permiso de leer la ficha del alumno): solo
 * muestra el seguimiento. El nombre llega como ayuda visual en la dirección; la API es quien decide el acceso a los datos.
 */
@Component({
  selector: 'fc-member-wellness-page',
  imports: [RouterLink, TranslocoPipe, MemberWellness],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="page-header">
      <div>
        <h1>{{ name() || ('wellness.title' | transloco) }}</h1>
        <p class="muted"><a routerLink="/trainers">{{ 'common.back' | transloco }}</a></p>
      </div>
    </header>
    <fc-member-wellness [memberId]="memberId()" />
  `,
})
export class MemberWellnessPage {
  private readonly route = inject(ActivatedRoute);

  private readonly params = toSignal(this.route.paramMap.pipe(map((p) => p.get('id') ?? '')), { initialValue: '' });
  private readonly query = toSignal(this.route.queryParamMap.pipe(map((q) => q.get('name') ?? '')), { initialValue: '' });

  protected readonly memberId = computed(() => this.params());
  protected readonly name = computed(() => this.query());
}
