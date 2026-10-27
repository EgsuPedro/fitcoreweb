import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'fc-forbidden',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card empty-state">
      <h1>{{ 'status.forbiddenTitle' | transloco }}</h1>
      <p class="muted">{{ 'status.forbiddenText' | transloco }}</p>
      <a class="btn btn-primary" routerLink="/">{{ 'status.backHome' | transloco }}</a>
    </section>
  `,
})
export class Forbidden {}

@Component({
  selector: 'fc-not-found',
  imports: [RouterLink, TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card empty-state">
      <h1>{{ 'status.notFoundTitle' | transloco }}</h1>
      <p class="muted">{{ 'status.notFoundText' | transloco }}</p>
      <a class="btn btn-primary" routerLink="/">{{ 'status.backHome' | transloco }}</a>
    </section>
  `,
})
export class NotFound {}
