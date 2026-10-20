using System.Text.Json;
using System.Xml.Linq;

namespace ConverterPoC.Tests;

public class ContributorsParserTests
{
    private static readonly XNamespace Crossref = TestRecords.Crossref;

    private const string Creators = """
        [
          { "person_or_org": { "type": "personal", "given_name": "Jane", "family_name": "Doe" } },
          { "person_or_org": { "type": "personal", "given_name": "John", "family_name": "Roe" }, "role": { "id": "datacollector" } }
        ]
        """;

    [Fact]
    public void Creators_AreAuthors_AndAllContributorsAreIncluded()
    {
        var contributors = Parse(Creators, """
            [
              { "person_or_org": { "type": "personal", "given_name": "Eddie", "family_name": "Tor" }, "role": { "id": "editor" } },
              { "person_or_org": { "type": "personal", "given_name": "Dana", "family_name": "Manager" }, "role": { "id": "datamanager" } }
            ]
            """);

        Assert.Equal(
            [("Jane", "author", "first"), ("John", "author", "additional"), ("Eddie", "editor", "additional"), ("Dana", "editor", "additional")],
            contributors.Elements(Crossref + "person_name").Select(p => (
                p.Element(Crossref + "given_name")!.Value,
                p.Attribute("contributor_role")!.Value,
                p.Attribute("sequence")!.Value)));
    }

    [Theory]
    [InlineData("""{ "id": "datacollector" }""", "author")]
    [InlineData("""{ "id": "researcher" }""", "author")]
    [InlineData("""{ "id": "projectleader" }""", "author")]
    [InlineData("""{ "id": "datacurator" }""", "editor")]
    [InlineData("""{ "id": "datamanager" }""", "editor")]
    [InlineData("""{ "id": "contactperson" }""", "editor")]
    [InlineData("""{ "id": "hostinginstitution" }""", "editor")]
    [InlineData("""{ "id": "unknown-role" }""", "editor")]
    [InlineData("""{}""", "editor")]
    public void Contributors_GetClosestCrossrefRole(string role, string crossrefRole)
    {
        var contributors = Parse(Creators, $$"""
            [{ "person_or_org": { "type": "personal", "given_name": "Some", "family_name": "Person" }, "role": {{role}} }]
            """);

        var person = contributors.Elements(Crossref + "person_name").Last();
        Assert.Equal("Person", person.Element(Crossref + "surname")!.Value);
        Assert.Equal(crossrefRole, person.Attribute("contributor_role")!.Value);
        Assert.Equal("additional", person.Attribute("sequence")!.Value);
    }

    [Fact]
    public void NoContributors_LeavesOnlyCreators()
    {
        var contributors = Parse(Creators, "[]");

        Assert.Equal(["Doe", "Roe"], contributors.Descendants(Crossref + "surname").Select(s => s.Value));
    }

    [Fact]
    public void OrganizationalCreator_IsOrganization()
    {
        var contributors = Parse("""[{ "person_or_org": { "type": "organizational", "name": "Test Organization" } }]""");

        var organization = Assert.Single(contributors.Elements(Crossref + "organization"));
        Assert.Equal("Test Organization", organization.Value);
        Assert.Equal("first", organization.Attribute("sequence")!.Value);
    }

    // These used to be swallowed, depositing the record without any authors
    [Theory]
    [InlineData("""[]""", "no creators")]
    [InlineData("""[{ "person_or_org": { "type": "personal" } }]""", "has no name")]
    [InlineData("""[{ "person_or_org": { "type": "organizational" } }]""", "organization without a name")]
    [InlineData("""[{ "person_or_org": { "name": "Missing type" } }]""", "unknown person_or_org type")]
    [InlineData("""[{ "name": "Not an InvenioRDM creator" }]""", "unknown person_or_org type")]
    public void UnreadableCreators_Throw(string creators, string message)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Parse(creators));

        Assert.Contains(message, exception.Message);
    }

    [Fact]
    public void UnreadableCreator_FailsTheConversion()
    {
        var json = TestRecords.Json(creators: """[{ "person_or_org": { "type": "personal" } }]""");

        Assert.Throws<InvalidOperationException>(() => TestRecords.Convert(json));
    }

    // Crossref requires a surname
    [Fact]
    public void PersonWithOnlyGivenName_IsDepositedUnderIt()
    {
        var person = Parse("""[{ "person_or_org": { "type": "personal", "given_name": "Plato" } }]""")
            .Elements(Crossref + "person_name").Single();

        Assert.Equal("Plato", person.Element(Crossref + "surname")!.Value);
        Assert.Equal("Plato", person.Element(Crossref + "given_name")!.Value);
    }

    [Fact]
    public void Affiliations_AndOneOrcid_AreIncluded()
    {
        var person = Parse("""
            [{
              "person_or_org": {
                "type": "personal", "given_name": "Jane", "family_name": "Doe",
                "identifiers": [
                  { "scheme": "orcid", "identifier": "0000-0002-1825-0097" },
                  { "scheme": "orcid", "identifier": "0000-0001-5109-3700" }
                ]
              },
              "affiliations": [{ "name": "Test University" }, { "id": "no-name" }]
            }]
            """).Elements(Crossref + "person_name").Single();

        Assert.Equal(["given_name", "surname", "affiliations", "ORCID"], person.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("Test University", person.Element(Crossref + "affiliations")!.Value);
        Assert.Equal("https://orcid.org/0000-0002-1825-0097", person.Element(Crossref + "ORCID")!.Value);
    }

    private static XElement Parse(string creators, string? contributors = null)
    {
        using var doc = JsonDocument.Parse(TestRecords.Json(creators: creators, contributors: contributors));

        return ContributorsParser.ConvertContributorsToXml(Crossref, doc.RootElement);
    }
}
