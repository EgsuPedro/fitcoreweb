# Esquemas XSD de la factura electrónica

| Archivo | Origen | Notas |
|---|---|---|
| `factura_V1.1.0.xsd` | Portal del SRI, *Esquemas XSD y XML tipo de documento Factura – versiones 1.0.0 – 1.1.0 – 2.0.0 – 2.1.0* (actualizado a febrero 2022), archivo `XML y XSD Factura.zip` → `factura_V1.1.0.xsd` | **Copia exacta, sin modificar** (SHA-256 `62db9bf0ecceb00ef2b7ed136e59224815e5e5e33c77efc6a0552001e052eb8b`). Es la versión que emitimos (`version="1.1.0"`: cantidad y precio unitario con hasta 6 decimales). La 2.x añade campos de guía de remisión sustitutiva que no usamos. |
| `xmldsig-core-schema.xsd` | W3C, <https://www.w3.org/TR/xmldsig-core/xmldsig-core-schema.xsd> (revisión 1.2, 2013/04/16) | El XSD del SRI lo importa (`schemaLocation="xmldsig-core-schema.xsd"`) para el elemento `ds:Signature`. Obtenido el 2026-10-30; transcrito por la herramienta de lectura web, por lo que **conviene compararlo con el original del W3C** (la validación de los comprobantes firmados del bloque 5 lo ejercita completo). |

Los esquemas se incrustan como recursos del ensamblado y se resuelven por nombre (sin red). No se editan: si el SRI publica una versión nueva, se agrega junto a esta y se cambia la versión que emite `InvoiceXmlBuilder`.
