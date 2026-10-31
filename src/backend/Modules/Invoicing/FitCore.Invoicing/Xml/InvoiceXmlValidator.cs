using System.Reflection;
using System.Xml;
using System.Xml.Schema;
using FitCore.Invoicing.Domain;

namespace FitCore.Invoicing.Xml;

/// <summary>
/// Valida un comprobante contra los esquemas oficiales (<c>factura_V1.1.0.xsd</c> o <c>notaCredito_V1.1.0.xsd</c> del SRI y el <c>xmldsig-core-schema.xsd</c> del W3C que éstos importan), incrustados en el
/// ensamblado. Cada tipo de documento tiene su propio juego de esquemas (declaran los mismos nombres de tipo y no pueden convivir). Nunca sale a la red: si un esquema pide un recurso externo, la carga falla en vez de descargarlo.
/// </summary>
public static class InvoiceXmlValidator
{
    private const string SignatureSchema = "xmldsig-core-schema.xsd";
    private const string SignatureNamespace = "http://www.w3.org/2000/09/xmldsig#";

    private static readonly Lazy<XmlSchemaSet> InvoiceSchemas = new(() => Load("factura_V1.1.0.xsd"), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<XmlSchemaSet> CreditNoteSchemas = new(() => Load("notaCredito_V1.1.0.xsd"), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Errores de validación (vacío si el XML cumple el esquema). Cada mensaje incluye la línea y la posición.</summary>
    public static IReadOnlyList<string> Validate(string xml, InvoiceDocumentType type = InvoiceDocumentType.Invoice)
    {
        var problems = new List<string>();
        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = (type == InvoiceDocumentType.CreditNote ? CreditNoteSchemas : InvoiceSchemas).Value,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationFlags = XmlSchemaValidationFlags.None
        };
        // Solo errores: dentro de ds:Object el esquema del W3C deja pasar contenido de otros espacios de nombres (las propiedades XAdES), cuyo esquema no se carga, y eso solo genera avisos.
        settings.ValidationEventHandler += (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error)
            {
                problems.Add($"error línea {e.Exception?.LineNumber}, pos. {e.Exception?.LinePosition}: {e.Message}");
            }
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
            }
        }
        catch (XmlException ex)
        {
            problems.Add($"error XML línea {ex.LineNumber}, pos. {ex.LinePosition}: {ex.Message}");
        }

        return problems;
    }

    private static XmlSchemaSet Load(string sriSchema)
    {
        var set = new XmlSchemaSet { XmlResolver = null };

        // El esquema de firma del W3C declara un DOCTYPE con entidades: se lee con DTD permitido, sin resolver nada externo, y solo es nuestro archivo incrustado.
        using (var dsig = Open(SignatureSchema))
        using (var reader = XmlReader.Create(dsig, new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null }, "embedded:///" + SignatureSchema))
        {
            set.Add(SignatureNamespace, reader);
        }

        // El del SRI importa «xmldsig-core-schema.xsd»: como ese espacio de nombres ya está cargado, no se busca de nuevo.
        using (var sri = new StringReader(Xml10(sriSchema)))
        using (var reader = XmlReader.Create(sri, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }, "embedded:///" + sriSchema))
        {
            set.Add(null, reader);
        }

        set.Compile();
        return set;
    }

    /// <summary>
    /// Texto del esquema incrustado. El del SRI para la nota de crédito declara <c>&lt;?xml version="1.1"?&gt;</c>, que .NET no admite; el resto de su contenido es XML 1.0. Para poder cargarlo se cambia
    /// <b>solo esa declaración, en memoria</b>: el archivo incrustado sigue siendo la copia exacta del SRI (una prueba comprueba su SHA-256).
    /// </summary>
    private static string Xml10(string name)
    {
        using var stream = Open(name);
        using var text = new StreamReader(stream, System.Text.Encoding.UTF8);
        var content = text.ReadToEnd();
        const string v11 = "<?xml version=\"1.1\"";
        return content.TrimStart('\uFEFF').StartsWith(v11, StringComparison.Ordinal) ? "<?xml version=\"1.0\"" + content.TrimStart('\uFEFF')[v11.Length..] : content;
    }

    internal static Stream Open(string name)
    {
        var assembly = typeof(InvoiceXmlValidator).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
        return assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Falta el esquema incrustado {name}.");
    }
}
