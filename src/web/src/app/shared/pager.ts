import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

@Component({
  selector: 'fc-pager',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (total() > 0) {
      <nav class="pager" [attr.aria-label]="'common.pagination' | transloco">
        <span class="muted">
          {{ 'common.showing' | transloco: { from: from(), to: to(), total: total() } }}
        </span>
        <span class="pager-buttons">
          <button type="button" class="btn" [disabled]="page() <= 1" (click)="pageChange.emit(page() - 1)">
            {{ 'common.previous' | transloco }}
          </button>
          <button type="button" class="btn" [disabled]="to() >= total()" (click)="pageChange.emit(page() + 1)">
            {{ 'common.next' | transloco }}
          </button>
        </span>
      </nav>
    }
  `,
})
export class Pager {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly total = input.required<number>();
  readonly pageChange = output<number>();

  protected readonly from = computed(() => (this.page() - 1) * this.pageSize() + 1);
  protected readonly to = computed(() => Math.min(this.page() * this.pageSize(), this.total()));
}
