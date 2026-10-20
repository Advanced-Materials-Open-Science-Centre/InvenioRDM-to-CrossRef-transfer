using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace ConverterPoC;

// Validates deposit XML against the Crossref schema, so a bad deposit fails before upload
// instead of in Crossref's asynchronous submission log. The schema files are downloaded from
// crossref.org the first time they're needed and cached in CacheDirectory.
public static class CrossrefSchema
{
    public const string Version = "5.5.0";
    public const string Namespace = "http://www.crossref.org/schema/" + Version;
    public const string FileName = "crossref" + Version + ".xsd";

    // Cached files keep their path relative to this location, so relative imports resolve the same way
    private static readonly Uri BaseUri = new("https://www.crossref.org/schemas/");

    public static readonly string CacheDirectory = Path.Combine(AppContext.BaseDirectory, "Schemas");

    private static readonly Lazy<XmlSchemaSet> Schemas = new(Compile);

    // Returns the schema errors; empty when the document is valid
    public static IReadOnlyList<string> Validate(string xml)
    {
        var errors = new List<string>();

        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = Schemas.Value,
            XmlResolver = null
        };

        settings.ValidationEventHandler += (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error)
                errors.Add($"line {e.Exception.LineNumber}: {e.Message}");
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read()) { }
        }
        catch (XmlException ex)
        {
            errors.Add($"Malformed XML: {ex.Message}");
        }

        return errors;
    }

    private static XmlSchemaSet Compile()
    {
        var schemas = new XmlSchemaSet { XmlResolver = new CachingSchemaResolver() };

        // The JATS modules report "Empty choice" warnings; only errors matter
        schemas.ValidationEventHandler += (_, e) =>
        {
            if (e.Severity == XmlSeverityType.Error)
                throw e.Exception;
        };

        schemas.Add(Namespace, new Uri(BaseUri, FileName).AbsoluteUri);
        schemas.Compile();

        return schemas;
    }

    private sealed class CachingSchemaResolver : XmlResolver
    {
        private static readonly Uri MathMlUri = new("http://www.w3.org/Math/XMLSchema/mathml3/");

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(1) };

        private static readonly XNamespace XmlSchemaNamespace = "http://www.w3.org/2001/XMLSchema";

        public override Uri ResolveUri(Uri? baseUri, string? relativeUri)
        {
            var resolved = base.ResolveUri(baseUri, relativeUri);

            // The Crossref schema imports MathML from w3.org, JATS the copy on crossref.org; loading
            // both would declare every MathML element twice
            return MathMlUri.IsBaseOf(resolved)
                ? new Uri(BaseUri, "standard-modules/mathml3/" + resolved.Segments[^1])
                : resolved;
        }

        public override object GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn)
        {
            var path = BaseUri.IsBaseOf(absoluteUri) ? BaseUri.MakeRelativeUri(absoluteUri).OriginalString : null;

            if (path == null || path.Contains(".."))
                throw new XmlSchemaException($"Schema {absoluteUri} is not under {BaseUri}");

            var file = Path.Combine(CacheDirectory, path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(file))
                Download(absoluteUri, file);

            return new MemoryStream(ToXsd10(File.ReadAllBytes(file)));
        }

        // .NET only reads XML 1.0 and XSD 1.0, but Crossref 5.5.0 schemas declare XML 1.1 and use XSD 1.1
        // assertions (e.g. that a role type belongs to its vocab). The assertions are dropped, so only
        // Crossref checks those rules.
        private static byte[] ToXsd10(byte[] content)
        {
            var text = Encoding.UTF8.GetString(content).TrimStart('﻿');
            text = Regex.Replace(text, @"^\s*<\?xml version=""1\.1""", @"<?xml version=""1.0""");

            if (!text.Contains(":assert"))
                return Encoding.UTF8.GetBytes(text);

            var schema = XDocument.Parse(text);
            schema.Descendants(XmlSchemaNamespace + "assert").Remove();

            return Encoding.UTF8.GetBytes(schema.ToString());
        }

        private static void Download(Uri uri, string file)
        {
            Console.WriteLine($"Downloading {uri}");

            byte[] content;

            try
            {
                content = Http.GetByteArrayAsync(uri).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new XmlSchemaException($"Could not download the Crossref schema {uri}: {ex.Message}", ex);
            }

            // Never cache an error page: it would break validation until deleted by hand
            if (!IsSchema(ToXsd10(content)))
                throw new XmlSchemaException($"{uri} is not an XML schema");

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            var temp = file + ".tmp";
            File.WriteAllBytes(temp, content);
            File.Move(temp, file, overwrite: true);
        }

        private static bool IsSchema(byte[] content)
        {
            try
            {
                return XDocument.Load(new MemoryStream(content)).Root?.Name.LocalName == "schema";
            }
            catch (XmlException)
            {
                return false;
            }
        }
    }
}
