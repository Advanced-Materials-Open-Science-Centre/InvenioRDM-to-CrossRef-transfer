using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ConverterPoC.Tests;

internal static class TestRecords
{
    public static readonly XNamespace Crossref = CrossrefSchema.Namespace;
    public static readonly XNamespace Jats = "http://www.ncbi.nlm.nih.gov/JATS1";

    public static readonly Depositor Depositor = new("Test Depositor", "depositor@example.org", "Test Registrant");

    public const string RepositoryName = "Test Repository";

    public static readonly ConversionSettings Settings = new(Depositor, RepositoryName);

    public const string PersonCreator =
        """[{ "person_or_org": { "type": "personal", "given_name": "Jane", "family_name": "Doe" } }]""";

    // Minimal InvenioRDM record; creators, contributors, identifiers, references and languages are
    // raw JSON arrays, customFields a raw JSON object
    public static string Json(
        string resourceType = "publication-book",
        string creators = PersonCreator,
        string? contributors = null,
        string? identifiers = null,
        string? references = null,
        string? description = "Test abstract",
        string? publicationDate = "2026-09-22",
        string? publisher = "Test Publisher",
        string? languages = null,
        string? customFields = null,
        bool filesEnabled = true)
    {
        var optional = new StringBuilder();

        if (contributors != null)
            optional.Append($""" "contributors": {contributors},""");

        if (identifiers != null)
            optional.Append($""" "identifiers": {identifiers},""");

        if (references != null)
            optional.Append($""" "references": {references},""");

        if (description != null)
            optional.Append($""" "description": {JsonSerializer.Serialize(description)},""");

        if (publicationDate != null)
            optional.Append($""" "publication_date": {JsonSerializer.Serialize(publicationDate)},""");

        if (publisher != null)
            optional.Append($""" "publisher": {JsonSerializer.Serialize(publisher)},""");

        if (languages != null)
            optional.Append($""" "languages": {languages},""");

        return $$"""
            {
              "files": { "enabled": {{(filesEnabled ? "true" : "false")}} },
              "custom_fields": {{customFields ?? "{}"}},
              "metadata": {
                "resource_type": { "id": "{{resourceType}}", "title": { "en": "Test type" } },
                "creators": {{creators}},
                {{optional}}
                "title": "Test record"
              }
            }
            """;
    }

    public static XDocument Convert(string json, CrossrefContentType? registeredType = null)
    {
        var xml = FromJsonConverter.Convert(
            Settings,
            json,
            "10.15330/test.26.09.01",
            "https://example.org/records/test",
            registeredType);

        Assert.NotNull(xml);

        return XDocument.Parse(xml);
    }
}
