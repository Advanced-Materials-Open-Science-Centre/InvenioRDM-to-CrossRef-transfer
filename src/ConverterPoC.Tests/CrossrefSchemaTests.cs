using System.Xml.Linq;

namespace ConverterPoC.Tests;

public class CrossrefSchemaTests
{
    private static readonly XNamespace Crossref = TestRecords.Crossref;

    [Theory]
    [InlineData("publication-book", null)]
    [InlineData("publication-article", null)]
    [InlineData("dataset", null)]
    [InlineData("presentation", null)]
    [InlineData("publication-book", CrossrefContentType.JournalArticle)]
    [InlineData("dataset", CrossrefContentType.PostedContent)]
    public void ConvertedRecord_IsValid(string resourceType, CrossrefContentType? registeredType)
    {
        var json = TestRecords.Json(
            resourceType: resourceType,
            identifiers: """[{ "identifier": "978-966-668-664-3", "scheme": "isbn" }]""",
            references: """[{ "reference": "Reference 1" }]""",
            description: "<p>Abstract with <strong>bold</strong> and x<sup>2</sup></p>",
            languages: """[{ "id": "ukr" }]""",
            contributors: """
                [
                  { "person_or_org": { "type": "personal", "family_name": "Editor" }, "role": { "id": "editor" } },
                  { "person_or_org": { "type": "personal", "family_name": "Curator" }, "role": { "id": "datacurator" } },
                  { "person_or_org": { "type": "organizational", "name": "Host" }, "role": { "id": "hostinginstitution" } }
                ]
                """,
            customFields: """
                { "journal:journal": { "title": "Test Journal", "issn": "1729-4428", "volume": "26", "issue": "1", "pages": "132-139" } }
                """,
            filesEnabled: false);

        Assert.Empty(CrossrefSchema.Validate(TestRecords.Convert(json, registeredType).ToString()));
    }

    [Fact]
    public void SchemaFiles_AreCachedLocally()
    {
        CrossrefSchema.Validate(TestRecords.Convert(TestRecords.Json()).ToString());

        Assert.True(File.Exists(Path.Combine(CrossrefSchema.CacheDirectory, CrossrefSchema.FileName)));
        Assert.True(File.Exists(Path.Combine(CrossrefSchema.CacheDirectory, "standard-modules", "mathml3", "mathml3.xsd")));
    }

    // Journal article whose publisher is the repository itself, deposited as posted content
    [Fact]
    public void JournalArticleWithoutJournal_IsValid()
    {
        var json = TestRecords.Json(resourceType: "publication-article", publisher: TestRecords.RepositoryName);

        Assert.Empty(CrossrefSchema.Validate(TestRecords.Convert(json).ToString()));
    }

    // The error Crossref returned for the original book deposit
    [Fact]
    public void BookWithoutIsbnOrNoisbn_IsRejected()
    {
        var doc = TestRecords.Convert(TestRecords.Json());
        doc.Descendants(Crossref + "noisbn").Remove();

        var error = Assert.Single(CrossrefSchema.Validate(doc.ToString()));

        Assert.Contains("publisher", error);
        Assert.Contains("noisbn", error);
    }

    [Fact]
    public void ElementWithoutNamespace_IsRejected()
    {
        var doc = TestRecords.Convert(TestRecords.Json());
        doc.Descendants(Crossref + "publisher").Single().AddBeforeSelf(new XElement("noisbn", new XAttribute("reason", "monograph")));
        doc.Descendants(Crossref + "noisbn").Remove();

        Assert.NotEmpty(CrossrefSchema.Validate(doc.ToString()));
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<doi_batch")]
    public void MalformedXml_IsReported(string xml)
    {
        Assert.Contains(CrossrefSchema.Validate(xml), e => e.StartsWith("Malformed XML"));
    }
}
