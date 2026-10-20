using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ConverterPoC;

// Converts an InvenioRDM record (the /api/records/{id} JSON) into a Crossref deposit.
// Helpers return null for optional elements that don't apply; XElement ignores null content.
public static class FromJsonConverter
{
    private static readonly XNamespace Crossref = CrossrefSchema.Namespace;
    private static readonly XNamespace Jats = "http://www.ncbi.nlm.nih.gov/JATS1";
    private static readonly XNamespace FundRef = CrossrefPrograms.FundRef;
    private static readonly XNamespace AccessIndicators = CrossrefPrograms.AccessIndicators;
    private static readonly XNamespace Relations = CrossrefPrograms.Relations;
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    // Throws when the record can't be converted, so the caller can report and skip it.
    // registeredType is the content type of an already registered DOI, which updates must keep.
    public static string Convert(
        ConversionSettings settings,
        string invenioRdmJson,
        string doi,
        string recordUrl,
        CrossrefContentType? registeredType = null)
    {
        using var record = JsonDocument.Parse(invenioRdmJson);

        var crossrefDoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Crossref + "doi_batch",
                new XAttribute(XNamespace.Xmlns + "ai", AccessIndicators.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "fr", FundRef.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "jats", Jats.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "rel", Relations.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
                new XAttribute("version", CrossrefSchema.Version),
                new XAttribute(Xsi + "schemaLocation",
                    $"{CrossrefSchema.Namespace} http://www.crossref.org/schemas/{CrossrefSchema.FileName}"),
                BuildHead(settings.Depositor),
                new XElement(Crossref + "body", BuildContent(record.RootElement, settings, doi, recordUrl, registeredType))
            )
        );

        var sb = new StringBuilder();

        using var sw = new Utf8StringWriter(sb);

        var writerSettings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = Encoding.UTF8,
            NewLineOnAttributes = true,
            OmitXmlDeclaration = false
        };

        using var writer = XmlWriter.Create(sw, writerSettings);

        crossrefDoc.WriteTo(writer);

        writer.Flush();
        return sb.ToString();
    }

    private static XElement BuildHead(Depositor depositor) =>
        new(Crossref + "head",
            new XElement(Crossref + "doi_batch_id", GenerateBatchId()),
            new XElement(Crossref + "timestamp", DateTime.UtcNow.ToString("yyyyMMddHHmmss")),
            new XElement(Crossref + "depositor",
                new XElement(Crossref + "depositor_name", depositor.Name),
                new XElement(Crossref + "email_address", depositor.Email)
            ),
            new XElement(Crossref + "registrant", depositor.Registrant)
        );

    private static XElement BuildContent(
        JsonElement root,
        ConversionSettings settings,
        string doi,
        string recordUrl,
        CrossrefContentType? registeredType)
    {
        var metadata = root.GetProperty("metadata");

        // Vocabulary id (e.g. "publication-book"); the display title can be renamed or translated
        var contentType = registeredType ??
                          CrossrefContentTypes.FromResourceType(metadata.GetProperty("resource_type").GetProperty("id").GetString());

        var journal = contentType == CrossrefContentType.JournalArticle ? GetJournal(root, metadata, settings) : null;

        if (contentType == CrossrefContentType.JournalArticle && journal == null)
        {
            if (registeredType != null)
                throw new InvalidOperationException(
                    "DOI is registered as a journal article, but the record has no journal title: fill in its Journal field in InvenioRDM");

            // A journal with the article's title would be wrong metadata; deposit it as a generic work
            Console.WriteLine("Warning: journal article has no journal title (Journal field or a publisher other than " +
                              $"{settings.RepositoryName}); depositing it as posted content");
            contentType = CrossrefContentType.PostedContent;
        }

        return contentType switch
        {
            CrossrefContentType.Book => CreateBook(root, metadata, doi, recordUrl),
            CrossrefContentType.JournalArticle => CreateJournal(root, metadata, journal!, doi, recordUrl),
            CrossrefContentType.Dataset => CreateDataset(root, metadata, settings, doi, recordUrl),
            _ => CreatePostedContent(root, metadata, doi, recordUrl)
        };
    }

    // Element order in each builder follows crossref5.5.0.xsd

    private static XElement CreateBook(JsonElement root, JsonElement metadata, string doi, string recordUrl) =>
        new(Crossref + "book",
            new XAttribute("book_type", "monograph"),
            new XElement(Crossref + "book_metadata",
                Language(metadata),
                Contributors(root),
                Titles(metadata),
                Abstracts(metadata),
                new XElement(Crossref + "publication_date", GetPublicationDate(metadata).ToCrossref(Crossref)),
                Isbns(metadata),
                Publisher(metadata),
                CrossrefPrograms.For(metadata),
                DoiData(doi, recordUrl),
                CitationList(metadata)
            )
        );

    // Schema 5.5.0 moved reports out of posted_content into their own record type, but registered
    // posted-content DOIs can't change type, so these stay posted content
    private static XElement CreatePostedContent(JsonElement root, JsonElement metadata, string doi, string recordUrl) =>
        new(Crossref + "posted_content",
            new XAttribute("type", "other"),
            Language(metadata),
            Contributors(root),
            Titles(metadata),
            new XElement(Crossref + "posted_date", GetPublicationDate(metadata).ToCrossref(Crossref)),
            Abstracts(metadata),
            CrossrefPrograms.For(metadata),
            DoiData(doi, recordUrl),
            CitationList(metadata)
        );

    private static XElement CreateJournal(JsonElement root, JsonElement metadata, Journal journal, string doi, string recordUrl)
    {
        var publicationDate = GetPublicationDate(metadata);

        return new XElement(Crossref + "journal",
            new XElement(Crossref + "journal_metadata",
                new XElement(Crossref + "full_title", journal.Title),
                journal.Issn != null ? new XElement(Crossref + "issn", journal.Issn) : null
            ),
            new XElement(Crossref + "journal_issue",
                new XElement(Crossref + "publication_date", new XElement(Crossref + "year", publicationDate.Year)),
                journal.Volume != null ? new XElement(Crossref + "journal_volume", new XElement(Crossref + "volume", journal.Volume)) : null,
                journal.Issue != null ? new XElement(Crossref + "issue", journal.Issue) : null
            ),
            new XElement(Crossref + "journal_article",
                // Default full_text; metadata-only records have no text to link to
                HasFiles(root) ? null : new XAttribute("publication_type", "bibliographic_record"),
                Language(metadata),
                Titles(metadata),
                ExtraJournalTitles(metadata),
                Contributors(root),
                Abstracts(metadata),
                new XElement(Crossref + "publication_date", publicationDate.ToCrossref(Crossref)),
                Pages(journal.Pages),
                CrossrefPrograms.For(metadata),
                DoiData(doi, recordUrl),
                CitationList(metadata)
            )
        );
    }

    // A dataset in the Crossref database for the whole repository
    private static XElement CreateDataset(
        JsonElement root,
        JsonElement metadata,
        ConversionSettings settings,
        string doi,
        string recordUrl)
    {
        var description = HtmlToJats.ToParagraphs(GetString(metadata, "description"), Jats);

        return new XElement(Crossref + "database",
            new XElement(Crossref + "database_metadata",
                new XElement(Crossref + "titles", new XElement(Crossref + "title", settings.RepositoryName))
            ),
            new XElement(Crossref + "dataset",
                new XAttribute("dataset_type", "record"),
                Contributors(root),
                Titles(metadata),
                new XElement(Crossref + "database_date",
                    new XElement(Crossref + "publication_date", GetPublicationDate(metadata).ToCrossref(Crossref))),
                description.Count > 0
                    ? new XElement(Crossref + "description",
                        Language(metadata),
                        string.Join("\n\n", description.Select(p => p.Value)))
                    : null,
                CrossrefPrograms.For(metadata),
                DoiData(doi, recordUrl),
                CitationList(metadata)
            )
        );
    }

    private record Journal(string Title, string? Issn, string? Volume, string? Issue, string? Pages);

    // From InvenioRDM's Journal custom field. This repository doesn't use it and depositors put the
    // journal name in publisher instead, so publisher is used unless it's the repository's own name.
    private static Journal? GetJournal(JsonElement root, JsonElement metadata, ConversionSettings settings)
    {
        var journalField = root.TryGetProperty("custom_fields", out var customFields) &&
                           customFields.ValueKind == JsonValueKind.Object &&
                           customFields.TryGetProperty("journal:journal", out var field)
            ? field
            : default;

        var publisher = GetString(metadata, "publisher");

        var title = GetString(journalField, "title") ??
                    (!string.IsNullOrWhiteSpace(publisher) &&
                     !publisher.Trim().Equals(settings.RepositoryName.Trim(), StringComparison.OrdinalIgnoreCase)
                        ? publisher.Trim()
                        : null);

        if (string.IsNullOrWhiteSpace(title))
            return null;

        var issn = GetString(journalField, "issn")?.Trim();

        return new Journal(
            title,
            issn != null && Regex.IsMatch(issn, @"^[0-9]{4}-?[0-9]{3}[0-9X]$") ? issn : null,
            NullIfBlank(GetString(journalField, "volume")),
            NullIfBlank(GetString(journalField, "issue")),
            NullIfBlank(GetString(journalField, "pages")));
    }

    // "132-139" -> first_page 132, last_page 139
    private static XElement? Pages(string? pages)
    {
        if (pages == null)
            return null;

        var parts = pages.Split(['-', '–'], 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return new XElement(Crossref + "pages",
            new XElement(Crossref + "first_page", parts[0]),
            parts.Length > 1 ? new XElement(Crossref + "last_page", parts[1]) : null);
    }

    private static bool HasFiles(JsonElement root) =>
        !(root.TryGetProperty("files", out var files) &&
          files.ValueKind == JsonValueKind.Object &&
          files.TryGetProperty("enabled", out var enabled) &&
          enabled.ValueKind == JsonValueKind.False);

    private static XAttribute? Language(JsonElement metadata) =>
        Languages.ToCrossref(metadata) is { } language ? new XAttribute("language", language) : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static XElement? Contributors(JsonElement root)
    {
        var contributors = ContributorsParser.ConvertContributorsToXml(Crossref, root);
        return contributors.HasElements ? contributors : null;
    }

    // Main title and subtitle. Crossref titles have no place for alternative or translated titles,
    // except that journal articles may carry several titles (ExtraJournalTitles).
    private static XElement? Titles(JsonElement metadata) =>
        metadata.TryGetProperty("title", out var title)
            ? new XElement(Crossref + "titles",
                new XElement(Crossref + "title", title.GetString()),
                AdditionalTitles(metadata, "subtitle").Take(1).Select(subtitle => new XElement(Crossref + "subtitle", subtitle)))
            : null;

    private static IEnumerable<XElement> ExtraJournalTitles(JsonElement metadata) =>
        AdditionalTitles(metadata, "alternative-title", "translated-title")
            .Take(19)
            .Select(title => new XElement(Crossref + "titles", new XElement(Crossref + "title", title)));

    private static IEnumerable<string> AdditionalTitles(JsonElement metadata, params string[] types) =>
        AdditionalEntries(metadata, "additional_titles", types)
            .Select(entry => NullIfBlank(GetString(entry, "title")))
            .OfType<string>();

    // The description, plus additional descriptions of type "abstract" (e.g. translations) in their language
    private static IEnumerable<XElement> Abstracts(JsonElement metadata)
    {
        var descriptions = new[] { (Html: GetString(metadata, "description"), Language: (string?)null) }
            .Concat(AdditionalEntries(metadata, "additional_descriptions", "abstract")
                .Select(entry => (GetString(entry, "description"), Languages.ToIso6391(GetString(GetObject(entry, "lang"), "id")))));

        foreach (var (html, language) in descriptions)
        {
            var paragraphs = HtmlToJats.ToParagraphs(html, Jats);

            if (paragraphs.Count > 0)
                yield return new XElement(Jats + "abstract",
                    language != null ? new XAttribute(XNamespace.Xml + "lang", language) : null,
                    paragraphs);
        }
    }

    // additional_titles/additional_descriptions entries whose type id is one of types
    private static IEnumerable<JsonElement> AdditionalEntries(JsonElement metadata, string property, params string[] types) =>
        metadata.TryGetProperty(property, out var entries) && entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray().Where(entry => types.Contains(GetString(GetObject(entry, "type"), "id")))
            : [];

    private static JsonElement GetObject(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    // publication_date/posted_date are required by Crossref, so a record without a usable date fails
    private static EdtfDate GetPublicationDate(JsonElement metadata)
    {
        var value = GetString(metadata, "publication_date");

        return EdtfDate.Parse(value) ??
               throw new InvalidOperationException($"Record has no valid EDTF publication_date: '{value}'");
    }

    // Crossref book_metadata requires either up to 6 <isbn> elements or a single <noisbn>
    private static IEnumerable<XElement> Isbns(JsonElement metadata)
    {
        var isbns = new List<string>();

        if (metadata.TryGetProperty("identifiers", out var identifiers) &&
            identifiers.ValueKind == JsonValueKind.Array)
        {
            foreach (var identifier in identifiers.EnumerateArray())
            {
                if (GetString(identifier, "scheme") != "isbn")
                    continue;

                var isbn = GetString(identifier, "identifier")?.Trim();

                if (IsCrossrefIsbn(isbn))
                    isbns.Add(isbn!);
                else
                    Console.WriteLine($"Skipping ISBN not accepted by Crossref schema: {isbn}");
            }
        }

        if (isbns.Count == 0)
            return [new XElement(Crossref + "noisbn", new XAttribute("reason", "monograph"))];

        return isbns.Distinct().Take(6)
            .Select(isbn => new XElement(Crossref + "isbn", new XAttribute("media_type", "print"), isbn));
    }

    // Mirrors isbn_t from common5.5.0.xsd
    private static bool IsCrossrefIsbn(string? isbn) =>
        !string.IsNullOrEmpty(isbn) &&
        isbn.Length is >= 10 and <= 17 &&
        Regex.IsMatch(isbn, @"^(97[89]-)?[0-9][0-9 \-]+[0-9X]$");

    private static XElement? Publisher(JsonElement metadata)
    {
        var publisher = GetString(metadata, "publisher");

        return string.IsNullOrEmpty(publisher)
            ? null
            : new XElement(Crossref + "publisher", new XElement(Crossref + "publisher_name", publisher));
    }

    private static XElement? DoiData(string doi, string recordUrl) =>
        string.IsNullOrEmpty(doi)
            ? null
            : new XElement(Crossref + "doi_data",
                new XElement(Crossref + "doi", doi),
                new XElement(Crossref + "resource", recordUrl));

    // InvenioRDM references are free text ({"reference": "..."}), deposited as unstructured citations
    private static XElement? CitationList(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("references", out var references) ||
            references.ValueKind != JsonValueKind.Array)
            return null;

        // Keys must be unique within the citation_list
        var citations = references.EnumerateArray()
            .Select(reference => GetString(reference, "reference"))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select((text, index) => new XElement(Crossref + "citation",
                new XAttribute("key", $"ref-{index + 1}"),
                new XElement(Crossref + "unstructured_citation", text)))
            .ToList();

        return citations.Count > 0 ? new XElement(Crossref + "citation_list", citations) : null;
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string GenerateBatchId()
    {
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var uniqueId = System.Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())));

        return $"{timestamp}-{uniqueId}";
    }
}
