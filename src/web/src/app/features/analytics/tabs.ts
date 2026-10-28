import { ChangeDetectionStrategy, Component, inject, input, output } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { map } from 'rxjs';

export interface TabDef {
  id: string;
  labelKey: string;
}

@Component({
  selector: 'fc-tab-bar',
  imports: [TranslocoPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tabs" role="tablist" [attr.aria-label]="label() | transloco">
      @for (t of tabs(); track t.id) {
        <button type="button" role="tab" class="tab" [class.active]="active() === t.id" [attr.aria-selected]="active() === t.id" (click)="select.emit(t.id)">
          {{ t.labelKey | transloco }}
        </button>
      }
    </div>
  `,
})
export class TabBar {
  readonly tabs = input.required<readonly TabDef[]>();
  readonly active = input.required<string>();
  readonly label = input('analytics.tabs.label');
  readonly select = output<string>();
}

/** La pestaña activa vive en la URL (`?tab=`): así un indicador del dashboard puede abrir su reporte exacto y recargar no pierde el lugar. */
export function queryTab(allowed: readonly string[], fallback: string) {
  const route = inject(ActivatedRoute);
  const router = inject(Router);
  const tab = toSignal(route.queryParamMap.pipe(map((q) => q.get('tab'))), { initialValue: route.snapshot.queryParamMap.get('tab') });
  return {
    current: () => {
      const value = tab();
      return value !== null && allowed.includes(value) ? value : fallback;
    },
    go: (id: string) => void router.navigate([], { relativeTo: route, queryParams: { tab: id }, queryParamsHandling: 'merge' }),
  };
}
