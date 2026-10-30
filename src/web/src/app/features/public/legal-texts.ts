/**
 * Versión vigente de los textos legales. Debe coincidir con `Legal:TermsVersion` y `Legal:PrivacyVersion` del servidor: al registrarse o enviar el formulario de contacto se
 * envía esta versión y el servidor rechaza una distinta. Al publicar un texto definitivo se cambian aquí, en el texto y en la configuración del servidor.
 */
export const LEGAL_VERSION = '2026-10-30-borrador';

export interface LegalSection {
  heading: string;
  paragraphs: string[];
}

/**
 * BORRADOR BASE, pendiente de revisión legal. No es asesoría jurídica. Quien opere el servicio debe reemplazar este texto por uno revisado por su abogado
 * antes de abrir el sitio al público. Está en español; la traducción al inglés está pendiente.
 */
export const TERMS_SECTIONS: LegalSection[] = [
  {
    heading: '1. Qué es FIT360',
    paragraphs: ['FIT360 es un servicio en línea para administrar gimnasios: alumnos, membresías, cobros, asistencia, clases, rutinas y reportes. Estos términos regulan su uso.'],
  },
  {
    heading: '2. Su cuenta',
    paragraphs: [
      'Quien se registra declara tener facultades para actuar en nombre del gimnasio y se compromete a dar datos verdaderos y a mantener en reserva sus contraseñas.',
      'El propietario es responsable de las personas a las que invita y de los permisos que les asigna.',
    ],
  },
  {
    heading: '3. Planes, pruebas y pagos',
    paragraphs: [
      'Cada plan tiene un precio, una periodicidad, unos límites y unas funciones que se muestran antes de contratarlo. Algunos planes incluyen un período de prueba gratuito, una sola vez por propietario.',
      'Registrarse nunca activa un plan de pago por sí solo. Las condiciones de cobro de los planes de pago se acuerdan con el equipo de FIT360.',
    ],
  },
  {
    heading: '4. Datos de su gimnasio y de sus alumnos',
    paragraphs: [
      'Los datos que usted carga siguen siendo suyos. FIT360 los trata solo para prestar el servicio y los mantiene aislados de los de otros gimnasios.',
      'Usted es responsable de contar con la autorización de sus alumnos para tratar sus datos, en especial los de salud (evaluaciones, nutrición, fotos).',
    ],
  },
  {
    heading: '5. Uso adecuado',
    paragraphs: ['Queda prohibido intentar acceder a datos de otros gimnasios, sobrecargar el servicio, o usarlo para fines ilícitos.'],
  },
  {
    heading: '6. Disponibilidad y cambios',
    paragraphs: [
      'Procuramos que el servicio esté disponible de forma continua, pero no garantizamos que no habrá interrupciones. Podemos mejorar o modificar funciones avisando con antelación razonable.',
      'Si estos términos cambian, se publicará una nueva versión y se le pedirá aceptarla.',
    ],
  },
  {
    heading: '7. Terminación',
    paragraphs: ['Puede dejar de usar el servicio cuando quiera. FIT360 puede suspender cuentas que incumplan estos términos.'],
  },
  {
    heading: '8. Contacto',
    paragraphs: ['Para cualquier consulta sobre estos términos, escríbanos desde la página de contacto.'],
  },
];

export const PRIVACY_SECTIONS: LegalSection[] = [
  {
    heading: '1. Quién es responsable',
    paragraphs: ['FIT360 trata los datos personales de quienes visitan el sitio, se registran o escriben por los formularios. Los datos de los alumnos de cada gimnasio los trata FIT360 por encargo del gimnasio.'],
  },
  {
    heading: '2. Qué datos recogemos y para qué',
    paragraphs: [
      'Formularios de contacto y demostración: nombre, correo, teléfono y nombre del gimnasio (opcionales los dos últimos) y su mensaje, para responderle. Se eliminan a los 180 días.',
      'Registro: nombre, correo y contraseña (guardada de forma irreversible), nombre del gimnasio y datos de contacto, para crear y operar su cuenta.',
      'No guardamos su dirección IP ni datos de su dispositivo en los formularios.',
    ],
  },
  {
    heading: '3. Datos de salud',
    paragraphs: ['Las evaluaciones, la nutrición y las fotos de progreso son datos sensibles. Solo las personas autorizadas por el gimnasio y con el consentimiento del alumno pueden verlos.'],
  },
  {
    heading: '4. Con quién los compartimos',
    paragraphs: ['No vendemos datos personales. Podemos usar proveedores de infraestructura y de correo para prestar el servicio, bajo obligaciones de confidencialidad.'],
  },
  {
    heading: '5. Sus derechos',
    paragraphs: ['Puede pedir acceso, rectificación, eliminación o limitación del tratamiento de sus datos escribiéndonos desde la página de contacto.'],
  },
  {
    heading: '6. Seguridad',
    paragraphs: ['Usamos conexiones cifradas, aislamiento entre gimnasios, contraseñas protegidas y registros de auditoría. Ningún sistema es infalible; si ocurriera un incidente que le afecte, se lo informaremos.'],
  },
];
