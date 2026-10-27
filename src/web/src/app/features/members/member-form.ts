import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';
import { ApiService } from '../../core/api/api.service';
import type { Member } from '../../core/api/models';
import { ErrorMessages } from '../../core/i18n/messages';
import { BranchCatalog } from '../../shared/branch-catalog';
import { ID_TYPES, IdType, isValidId } from '../../shared/ecuador-id';
import { Modal } from '../../shared/modal';
import { ToastService } from '../../shared/toast';

const PHONE = /^[+0-9][0-9 ()-]{6,29}$/;

/** Formulario de alta y edición de alumnos (en un diálogo), usado por el listado y por la ficha del alumno. */
@Component({
  selector: 'fc-member-form',
  imports: [ReactiveFormsModule, TranslocoPipe, Modal],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <fc-modal [title]="(member() ? 'members.edit' : 'members.new') | transloco" (closed)="closed.emit()">
      <form id="member-form" [formGroup]="form" (ngSubmit)="save()" novalidate>
        @if (formError()) {
          <p class="alert alert-danger" role="alert">{{ formError() }}</p>
        }
        <div class="form-grid">
          @if (!member()) {
            <label class="field">
              <span>{{ 'common.branch' | transloco }}</span>
              <select formControlName="branchId">
                @for (branch of branches(); track branch.id) {
                  <option [value]="branch.id">{{ branch.name }}</option>
                }
              </select>
            </label>
          }
          <label class="field">
            <span>{{ 'members.firstName' | transloco }}</span>
            <input formControlName="firstName" maxlength="100" autocomplete="off" />
          </label>
          <label class="field">
            <span>{{ 'members.lastName' | transloco }}</span>
            <input formControlName="lastName" maxlength="100" autocomplete="off" />
          </label>
          <label class="field">
            <span>{{ 'members.idType' | transloco }}</span>
            <select formControlName="idType">
              @for (type of idTypes; track type) {
                <option [value]="type">{{ 'enums.idType.' + type | transloco }}</option>
              }
            </select>
          </label>
          <label class="field">
            <span>{{ 'members.idNumber' | transloco }}</span>
            <input formControlName="idNumber" maxlength="30" autocomplete="off" />
            @if (form.errors?.['id'] && form.controls.idNumber.touched) {
              <small class="field-error" role="alert">{{ 'members.invalidId' | transloco }}</small>
            }
          </label>
          <label class="field">
            <span>{{ 'members.birthDate' | transloco }}</span>
            <input type="date" formControlName="birthDate" />
          </label>
          <label class="field">
            <span>{{ 'members.email' | transloco }}</span>
            <input type="email" formControlName="email" maxlength="256" autocomplete="off" />
          </label>
          <label class="field">
            <span>{{ 'members.phone' | transloco }}</span>
            <input type="tel" formControlName="phone" maxlength="30" autocomplete="off" />
          </label>
          <label class="field">
            <span>{{ 'members.address' | transloco }}</span>
            <input formControlName="address" maxlength="300" />
          </label>
          <label class="field">
            <span>{{ 'members.photoUrl' | transloco }}</span>
            <input type="url" formControlName="photoUrl" maxlength="500" placeholder="https://" />
          </label>
        </div>
        <h3>{{ 'members.emergency' | transloco }}</h3>
        <div class="form-grid">
          <label class="field">
            <span>{{ 'members.emergencyName' | transloco }}</span>
            <input formControlName="emergencyContactName" maxlength="150" />
          </label>
          <label class="field">
            <span>{{ 'members.emergencyPhone' | transloco }}</span>
            <input type="tel" formControlName="emergencyContactPhone" maxlength="30" />
          </label>
          <label class="field">
            <span>{{ 'members.emergencyRelationship' | transloco }}</span>
            <input formControlName="emergencyContactRelationship" maxlength="50" />
          </label>
        </div>
        <label class="field">
          <span>{{ 'members.notes' | transloco }}</span>
          <textarea formControlName="notes" maxlength="1000"></textarea>
        </label>
      </form>
      <ng-container footer>
        <button type="button" class="btn" (click)="closed.emit()">{{ 'common.cancel' | transloco }}</button>
        <button type="submit" form="member-form" class="btn btn-primary" [disabled]="saving()">{{ 'common.save' | transloco }}</button>
      </ng-container>
    </fc-modal>
  `,
})
export class MemberForm implements OnInit {
  private readonly api = inject(ApiService);
  private readonly errors = inject(ErrorMessages);
  private readonly toast = inject(ToastService);
  private readonly fb = inject(FormBuilder);
  private readonly catalog = inject(BranchCatalog);

  /** Alumno a editar; sin él, es un alta. */
  readonly member = input<Member | null>(null);
  readonly saved = output<Member>();
  readonly closed = output<void>();

  protected readonly idTypes = ID_TYPES;
  protected readonly branches = this.catalog.branches;
  protected readonly saving = signal(false);
  protected readonly formError = signal<string | null>(null);

  protected readonly form = this.fb.nonNullable.group(
    {
      branchId: [''],
      firstName: ['', [Validators.required, Validators.maxLength(100)]],
      lastName: ['', [Validators.required, Validators.maxLength(100)]],
      idType: ['Cedula' as IdType, [Validators.required]],
      idNumber: ['', [Validators.required, Validators.maxLength(30)]],
      birthDate: [''],
      email: ['', [Validators.email, Validators.maxLength(256)]],
      phone: ['', [Validators.pattern(PHONE)]],
      address: ['', [Validators.maxLength(300)]],
      photoUrl: ['', [Validators.pattern(/^https?:\/\/\S+$/i), Validators.maxLength(500)]],
      emergencyContactName: ['', [Validators.maxLength(150)]],
      emergencyContactPhone: ['', [Validators.pattern(PHONE)]],
      emergencyContactRelationship: ['', [Validators.maxLength(50)]],
      notes: ['', [Validators.maxLength(1000)]],
    },
    {
      // Respuesta inmediata sobre el documento; el backend aplica las mismas reglas y es la autoridad.
      validators: [
        (group): ValidationErrors | null => {
          const { idType, idNumber } = group.getRawValue() as { idType: IdType; idNumber: string };
          return idNumber && !isValidId(idType, idNumber) ? { id: true } : null;
        },
      ],
    },
  );

  ngOnInit(): void {
    this.catalog.ensure();
    const member = this.member();
    if (member) {
      this.form.patchValue({
        firstName: member.firstName,
        lastName: member.lastName,
        idType: member.idType as IdType,
        idNumber: member.idNumber,
        birthDate: member.birthDate ?? '',
        email: member.email ?? '',
        phone: member.phone ?? '',
        address: member.address ?? '',
        photoUrl: member.photoUrl ?? '',
        emergencyContactName: member.emergencyContactName ?? '',
        emergencyContactPhone: member.emergencyContactPhone ?? '',
        emergencyContactRelationship: member.emergencyContactRelationship ?? '',
        notes: member.notes ?? '',
      });
    } else {
      const branches = this.branches();
      this.form.controls.branchId.setValue(branches[0]?.id ?? '');
      this.form.controls.branchId.addValidators(Validators.required);
    }
  }

  protected save(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      this.formError.set(null);
      return;
    }

    const v = this.form.getRawValue();
    const nullable = (text: string): string | null => text.trim() || null;
    const profile = {
      firstName: v.firstName.trim(),
      lastName: v.lastName.trim(),
      idType: v.idType,
      idNumber: v.idNumber.trim(),
      birthDate: nullable(v.birthDate),
      email: nullable(v.email),
      phone: nullable(v.phone),
      address: nullable(v.address),
      photoUrl: nullable(v.photoUrl),
      emergencyContactName: nullable(v.emergencyContactName),
      emergencyContactPhone: nullable(v.emergencyContactPhone),
      emergencyContactRelationship: nullable(v.emergencyContactRelationship),
      notes: nullable(v.notes),
    };

    this.saving.set(true);
    this.formError.set(null);
    const member = this.member();
    const request = member ? this.api.updateMember(member.id, profile) : this.api.createMember({ branchId: v.branchId, ...profile });
    request.subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.toast.success('members.savedToast');
        this.saved.emit(saved);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        this.formError.set(this.errors.message(err));
      },
    });
  }
}
