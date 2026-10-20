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
            [
                ("Jane", "crossref:author", "first"),
                ("John", "crossref:author credit:investigation", "additional"),
                ("Eddie", "crossref:editor", "additional"),
                ("Dana", "credit:data-curation", "additional")
            ],
            contributors.Elements(Crossref + "person_name").Select(p => (
                p.Element(Crossref + "given_name")!.Value,
                Roles(p),
                p.Attribute("sequence")!.Value)));
    }

    [Theory]
    [InlineData("""{ "id": "editor" }""", "crossref:editor")]
    [InlineData("""{ "id": "contactperson" }""", "crossref:corresponding-author")]
    [InlineData("""{ "id": "datacurator" }""", "credit:data-curation")]
    [InlineData("""{ "id": "datamanager" }""", "credit:data-curation")]
    [InlineData("""{ "id": "datacollector" }""", "credit:investigation")]
    [InlineData("""{ "id": "researcher" }""", "credit:investigation")]
    [InlineData("""{ "id": "projectleader" }""", "credit:supervision")]
    [InlineData("""{ "id": "projectmanager" }""", "credit:project-administration")]
    [InlineData("""{ "id": "hostinginstitution" }""", "credit:resources")]
    [InlineData("""{ "id": "sponsor" }""", "credit:funding-acquisition")]
    [InlineData("""{ "id": "rightsholder" }""", "crossref:other")]
    [InlineData("""{ "id": "unknown-role" }""", "crossref:other")]
    [InlineData("""{}""", "crossref:other")]
    public void Contributors_GetClosestRole(string role, string expected)
    {
        var contributors = Parse(Creators, $$"""
            [{ "person_or_org": { "type": "personal", "given_name": "Some", "family_name": "Person" }, "role": {{role}} }]
            """);

        var person = contributors.Elements(Crossref + "person_name").Last();
        Assert.Equal("Person", person.Element(Crossref + "surname")!.Value);
        Assert.Equal(expected, Roles(person));
        Assert.Equal("additional", person.Attribute("sequence")!.Value);
    }

    [Theory]
    [InlineData("""{ "id": "editor" }""", "editor")]
    [InlineData("""{ "id": "hostinginstitution" }""", "other")]
    [InlineData("""{}""", "other")]
    public void OrganizationalContributor_GetsCrossrefRoleAttribute(string role, string expected)
    {
        var contributors = Parse(Creators, $$"""
            [{ "person_or_org": { "type": "organizational", "name": "Test Organization" }, "role": {{role}} }]
            """);

        var organization = Assert.Single(contributors.Elements(Crossref + "organization"));
        Assert.Equal(expected, organization.Attribute("contributor_role")!.Value);
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
        Assert.Equal("author", organization.Attribute("contributor_role")!.Value);
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

        Assert.Equal(["given_name", "surname", "role", "affiliations", "ORCID"], person.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("Test University", person.Element(Crossref + "affiliations")!.Value);
        Assert.Equal("https://orcid.org/0000-0002-1825-0097", person.Element(Crossref + "ORCID")!.Value);
    }

    private static string Roles(XElement person) =>
        string.Join(" ", person.Elements(Crossref + "role").Select(r => $"{r.Attribute("vocab")!.Value}:{r.Attribute("type")!.Value}"));

    private static XElement Parse(string creators, string? contributors = null)
    {
        using var doc = JsonDocument.Parse(TestRecords.Json(creators: creators, contributors: contributors));

        return ContributorsParser.ConvertContributorsToXml(Crossref, doc.RootElement);
    }
}
