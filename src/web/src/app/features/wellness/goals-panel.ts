import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Goal } from '../../core/api/models';
import { Perm } from '../../core/auth/permissions';
import { SessionService } from '../../core/auth/session.service';
import { ErrorMessages } from '../../core/i18n/messages';
import { Badge } from '../../shared/badge';
import { clampPercent } from '../../shared/chart-math';
import { FcDate, FcNumber } from '../../shared/format.pipes';
import { toNumber } from '../../shared/form-utils';
import { Modal } from '../../shared/modal';
import { ToastService } from '../../shared/toast';

const BODY_METRICS = ['WeightKg', 'WaistCm', 'HipCm', 'ChestCm', 'ArmCm', 'ThighCm', 'BodyFatPercent'] as const;

/**
 * Objetivos del alumno. Los define siempre una persona (el sistema no propone metas a partir del peso ni la apariencia), el
 * progreso se calcula con las mediciones registradas y el objetivo no se cierra solo: al alcanzar la meta lo cierra una persona.
 */
@Component({
  selector: 'fc-goals-panel',
  imports: [TranslocoPipe, Badge, FcDate, FcNumber, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error()) {
      <p class="alert alert-danger" role="alert">{{ error() }}</p>
    }

    @if (canManage()) {
      <div class="inline-actions">
        <button type="button" class="btn btn-primary" (click)="openCreate()">{{ 'goals.new' | transloco }}</button>
      </div>
    }

    @for (g of goals(); track g.id) {
      <div class="subcard">
        <div class="bar-row">
          <strong>{{ g.title }}</strong>
          <span></span>
          <span>
            @if (g.status === 'Active') {
              @if (g.reached) {
                <fc-badge tone="ok">{{ 'goals.reached' | transloco }}</fc-badge>
              } @else if (g.overdue) {
                <fc-badge tone="warn">{{ 'goals.overdue' | transloco }}</fc-badge>
              } @else {
                <fc-badge tone="info">{{ 'goals.active' | transloco }}</fc-badge>
              }
            } @else {
              <fc-badge [tone]="g.status === 'Achieved' ? 'ok' : 'neutral'">{{ 'enums.goalStatus.' + g.status | transloco }}</fc-badge>
            }
          </span>
        </div>
        @if (g.description) {
          <p class="muted">{{ g.description }}</p>
        }
        @if (g.progressPercent !== null && g.progressPercent !== undefined) {
          <div class="meter-row">
            <span>{{ 'goals.progress' | transloco }}</span>
            <span class="meter" [class.meter-ok]="g.reached" role="progressbar" [attr.aria-valuenow]="g.progressPercent" aria-valuemin="0" aria-valuemax="100" [attr.aria-label]="g.title">
              <span [style.--w]="pct(g)"></span>
            </span>
            <span class="num">{{ g.progressPercent | fcNumber: 0 }} %</span>
          </div>
        } @else if (g.category !== 'Custom') {
          <p class="muted"><small>{{ 'goals.noProgress' | transloco }}</small></p>
        }
        <dl class="kv">
          @if (g.metric) {
            <dt>{{ 'goals.metric' | transloco }}</dt>
            <dd>{{ 'goals.metrics.' + g.metric | transloco }}</dd>
            <dt>{{ 'goals.baseline' | transloco }}</dt>
            <dd>{{ g.baselineValue | fcNumber }} {{ g.unit }}</dd>
            <dt>{{ 'goals.current' | transloco }}</dt>
            <dd>{{ g.currentValue | fcNumber }} {{ g.unit }}@if (g.currentAsOf) { <small class="muted"> · {{ g.currentAsOf | fcDate }}</small> }</dd>
            <dt>{{ 'goals.target' | transloco }}</dt>
            <dd>{{ g.targetValue | fcNumber }} {{ g.unit }}</dd>
          }
          <dt>{{ 'goals.start' | transloco }}</dt>
          <dd>{{ g.startDate | fcDate }}</dd>
          <dt>{{ 'goals.targetDate' | transloco }}</dt>
          <dd>{{ g.targetDate | fcDate }}</dd>
          @if (g.closedOn) {
            <dt>{{ 'goals.closedOn' | transloco }}</dt>
            <dd>{{ g.closedOn | fcDate }}@if (g.closeNote) { · {{ g.closeNote }} }</dd>
          }
        </dl>
        @if (canManage() && g.status === 'Active') {
          <div class="inline-actions"><button type="button" class="btn" (click)="openClose(g)">{{ 'goals.close' | transloco }}</button></div>
        }
      </div>
    } @empty {
      <p class="muted">{{ 'goals.empty' | transloco }}</p>
    }
    <p class="muted"><small>{{ 'goals.note' | transloco }}</small></p>

    @if (creating()) {
      <fc-modal [title]="'goals.new' | transloco" (closed)="creating.set(false)">
        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <label class="field"><span>{{ 'goals.titleLabel' | transloco }}</span><input maxlength="120" [value]="form().title" (input)="patch('title', $any($event.target).value)" /></label>
        <label class="field"><span>{{ 'common.notes' | transloco }}</span><textarea maxlength="1000" [value]="form().description" (input)="patch('description', $any($event.target).value)"></textarea></label>
        <label class="field">
          <span>{{ 'goals.category' | transloco }}</span>
          <select [value]="form().category" (change)="patch('category', $any($event.target).value)">
            @for (c of categories; track c) {
              <option [value]="c">{{ 'enums.goalCategory.' + c | transloco }}</option>
            }
          </select>
        </label>
        @if (form().category === 'BodyMetric') {
          <label class="field">
            <span>{{ 'goals.metric' | transloco }}</span>
            <select [value]="form().metric" (change)="patch('metric', $any($event.target).value)">
              @for (m of bodyMetrics; track m) {
                <option [value]="m">{{ 'goals.metrics.' + m | transloco }}</option>
              }
            </select>
          </label>
        }
        @if (form().category !== 'Custom') {
          <label class="field">
            <span>{{ form().category === 'SessionsPerWeek' ? ('goals.sessionsTarget' | transloco) : ('goals.target' | transloco) }}</span>
            <input inputmode="decimal" [value]="form().target" (input)="patch('target', $any($event.target).value)" />
          </label>
        }
        @if (form().category === 'BodyMetric') {
          <label class="field">
            <span>{{ 'goals.baseline' | transloco }}</span>
            <input inputmode="decimal" [value]="form().baseline" (input)="patch('baseline', $any($event.target).value)" />
            <small class="muted">{{ 'goals.baselineHint' | transloco }}</small>
          </label>
        }
        <label class="field"><span>{{ 'goals.targetDate' | transloco }}</span><input type="date" [value]="form().targetDate" (input)="patch('targetDate', $any($event.target).value)" /></label>
        <ng-container footer>
          <button type="button" class="btn" (click)="creating.set(false)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="create()">{{ 'common.save' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }

    @if (closing(); as g) {
      <fc-modal [title]="'goals.closeTitle' | transloco: { title: g.title }" (closed)="closing.set(null)">
        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <label class="field">
          <span>{{ 'goals.closeAs' | transloco }}</span>
          <select [value]="closeStatus()" (change)="closeStatus.set($any($event.target).value)">
            <option value="Achieved">{{ 'enums.goalStatus.Achieved' | transloco }}</option>
            <option value="Abandoned">{{ 'enums.goalStatus.Abandoned' | transloco }}</option>
          </select>
        </label>
        <label class="field">
          <span>{{ closeStatus() === 'Abandoned' ? ('goals.reasonRequired' | transloco) : ('common.notes' | transloco) }}</span>
          <textarea maxlength="1000" [value]="closeNote()" (input)="closeNote.set($any($event.target).value)"></textarea>
        </label>
        <ng-container footer>
          <button type="button" class="btn" (click)="closing.set(null)">{{ 'common.cancel' | transloco }}</button>
          <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="close(g)">{{ 'goals.close' | transloco }}</button>
        </ng-container>
      </fc-modal>
    }
  `,
})
export class GoalsPanel implements OnInit {
  private readonly api = inject(ApiService);
  private readonly session = inject(SessionService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);

  readonly memberId = input.required<string>();

  protected readonly categories = ['BodyMetric', 'SessionsPerWeek', 'Custom'] as const;
  protected readonly bodyMetrics = BODY_METRICS;
  protected readonly goals = signal<Goal[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly formError = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly creating = signal(false);
  protected readonly closing = signal<Goal | null>(null);
  protected readonly closeStatus = signal<'Achieved' | 'Abandoned'>('Achieved');
  protected readonly closeNote = signal('');
  protected readonly canManage = computed(() => this.session.has(Perm.GoalsManage));
  protected readonly form = signal(this.emptyForm());

  ngOnInit(): void {
    this.reload();
  }

  protected pct(g: Goal): number {
    return clampPercent(g.progressPercent);
  }

  protected patch(key: string, value: string): void {
    this.form.update((f) => ({ ...f, [key]: value }));
  }

  protected openCreate(): void {
    this.form.set(this.emptyForm());
    this.formError.set(null);
    this.creating.set(true);
  }

  protected openClose(goal: Goal): void {
    this.closeStatus.set(goal.reached ? 'Achieved' : 'Abandoned');
    this.closeNote.set('');
    this.formError.set(null);
    this.closing.set(goal);
  }

  protected create(): void {
    const f = this.form();
    this.busy.set(true);
    this.formError.set(null);
    this.api
      .createGoal(this.memberId(), {
        title: f.title.trim(),
        description: f.description.trim() || null,
        category: f.category as 'Custom',
        metric: f.category === 'BodyMetric' ? (f.metric as 'WeightKg') : null,
        targetValue: f.category === 'Custom' ? null : toNumber(f.target),
        baselineValue: f.category === 'BodyMetric' ? toNumber(f.baseline) : null,
        targetDate: f.targetDate || null,
      })
      .subscribe({
        next: () => {
          this.busy.set(false);
          this.creating.set(false);
          this.toast.success('goals.createdToast');
          this.reload();
        },
        error: (err: unknown) => {
          this.busy.set(false);
          this.formError.set(this.errors.message(err));
        },
      });
  }

  protected close(goal: Goal): void {
    this.busy.set(true);
    this.formError.set(null);
    this.api.closeGoal(goal.id, { status: this.closeStatus(), note: this.closeNote().trim() || null }).subscribe({
      next: () => {
        this.busy.set(false);
        this.closing.set(null);
        this.toast.success('goals.closedToast');
        this.reload();
      },
      error: (err: unknown) => {
        this.busy.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }

  private reload(): void {
    this.api.goals(this.memberId()).subscribe({
      next: (list) => this.goals.set(list),
      error: (err: unknown) => this.error.set(this.errors.message(err)),
    });
  }

  private emptyForm() {
    return { title: '', description: '', category: 'BodyMetric', metric: 'WeightKg', target: '', baseline: '', targetDate: '' };
  }
}
