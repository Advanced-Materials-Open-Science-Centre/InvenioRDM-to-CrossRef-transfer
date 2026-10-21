using System.Text.Json;
using System.Xml.Linq;

namespace ConverterPoC;

// Creators become authors; contributors get the Crossref or CRediT role closest to their InvenioRDM one.
// Throws on entries it can't read, so a record never silently loses its authors.
public class ContributorsParser
{
    private record Role(string Type, string Vocab);

    private static readonly Role Author = new("author", "crossref");
    private static readonly Role Other = new("other", "crossref");

    // By InvenioRDM (DataCite) contributor role id; roles not listed become "other"
    private static readonly Dictionary<string, Role> Roles = new()
    {
        ["editor"] = new("editor", "crossref"),
        ["contactperson"] = new("corresponding-author", "crossref"),
        ["datacurator"] = new("data-curation", "credit"),
        ["datamanager"] = new("data-curation", "credit"),
        ["datacollector"] = new("investigation", "credit"),
        ["researcher"] = new("investigation", "credit"),
        ["researchgroup"] = new("investigation", "credit"),
        ["projectmember"] = new("investigation", "credit"),
        ["projectleader"] = new("supervision", "credit"),
        ["supervisor"] = new("supervision", "credit"),
        ["projectmanager"] = new("project-administration", "credit"),
        ["workpackageleader"] = new("project-administration", "credit"),
        ["hostinginstitution"] = new("resources", "credit"),
        ["sponsor"] = new("funding-acquisition", "credit"),
        ["funder"] = new("funding-acquisition", "credit"),
    };

    public static XElement ConvertContributorsToXml(XNamespace nameSpace, JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metadata))
            throw new InvalidOperationException("No metadata found in the InvenioRDM JSON");

        // Creators are the authors of the work; their InvenioRDM role, if any, is added to that
        var entries = Array(metadata, "creators")
            .Select(creator => new Entry([creator], Roles.TryGetValue(RoleId(creator), out var role) ? [Author, role] : [Author]))
            .ToList();

        if (entries.Count == 0)
            throw new InvalidOperationException("Record has no creators");

        // Depositors often list creators again as contributors (e.g. to give them a role or an ORCID);
        // such a contributor is merged into that entry instead of appearing twice
        foreach (var contributor in Array(metadata, "contributors"))
        {
            var role = Roles.GetValueOrDefault(RoleId(contributor), Other);
            var same = entries.FirstOrDefault(entry => IsSame(entry.Records[0], contributor));

            if (same == null)
            {
                entries.Add(new Entry([contributor], [role]));
                continue;
            }

            same.Records.Add(contributor);

            if (!same.Roles.Contains(role))
                same.Roles.Add(role);
        }

        return new XElement(nameSpace + "contributors",
            entries.Select((entry, index) =>
                ToXml(nameSpace, entry, index == 0 ? "first" : "additional", index + 1)));
    }

    // The InvenioRDM creator/contributor entries of one person or organization, first one leading
    private record Entry(List<JsonElement> Records, List<Role> Roles);

    private static string RoleId(JsonElement contributor) => String(Object(contributor, "role"), "id") ?? "";

    // Same type and name; people with different ORCIDs are different people despite the name
    private static bool IsSame(JsonElement a, JsonElement b)
    {
        var (personA, personB) = (Object(a, "person_or_org"), Object(b, "person_or_org"));
        var type = String(personA, "type");

        if (type != String(personB, "type"))
            return false;

        if (type == "organizational")
            return SameName(String(personA, "name"), String(personB, "name"));

        var (orcidA, orcidB) = (Orcid(personA), Orcid(personB));

        if (orcidA != null && orcidB != null)
            return orcidA == orcidB;

        var (givenA, familyA) = (String(personA, "given_name"), String(personA, "family_name"));

        return Normalize(givenA + familyA) != "" &&
               SameName(givenA, String(personB, "given_name")) &&
               SameName(familyA, String(personB, "family_name"));
    }

    private static bool SameName(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? name) => string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Orcid(JsonElement personOrOrg) =>
        Array(personOrOrg, "identifiers")
            .Where(identifier => string.Equals(String(identifier, "scheme"), "orcid", StringComparison.OrdinalIgnoreCase))
            .Select(identifier => String(identifier, "identifier")?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static XElement ToXml(XNamespace ns, Entry entry, string sequence, int position)
    {
        var roles = entry.Roles;
        var personOrOrg = Object(entry.Records[0], "person_or_org");
        var type = String(personOrOrg, "type");

        if (type == "organizational")
        {
            var name = String(personOrOrg, "name");

            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException($"Creator/contributor {position} is an organization without a name");

            // Organizations can't have role elements, only a single role from the Crossref vocabulary
            return new XElement(ns + "organization",
                new XAttribute("sequence", sequence),
                new XAttribute("contributor_role", (roles.FirstOrDefault(r => r.Vocab == "crossref") ?? Other).Type),
                name);
        }

        if (type != "personal")
            throw new InvalidOperationException($"Creator/contributor {position} has unknown person_or_org type '{type}'");

        var givenName = String(personOrOrg, "given_name");
        var familyName = String(personOrOrg, "family_name");

        if (string.IsNullOrWhiteSpace(givenName) && string.IsNullOrWhiteSpace(familyName))
            throw new InvalidOperationException($"Creator/contributor {position} has no name");

        var institutions = entry.Records
            .SelectMany(record => Array(record, "affiliations"))
            .Select(affiliation => String(affiliation, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .DistinctBy(name => Normalize(name).ToUpperInvariant())
            .Select(name => new XElement(ns + "institution", new XElement(ns + "institution_name", name)))
            .ToList();

        // Crossref takes a single ORCID per person
        var orcid = entry.Records
            .Select(record => Orcid(Object(record, "person_or_org")))
            .FirstOrDefault(value => value != null);

        return new XElement(ns + "person_name",
            new XAttribute("sequence", sequence),
            string.IsNullOrWhiteSpace(givenName) ? null : new XElement(ns + "given_name", givenName),
            // Crossref requires a surname; a person with only a given name is deposited under it
            new XElement(ns + "surname", string.IsNullOrWhiteSpace(familyName) ? givenName : familyName),
            roles.Select(r => new XElement(ns + "role", new XAttribute("type", r.Type), new XAttribute("vocab", r.Vocab))),
            institutions.Count > 0 ? new XElement(ns + "affiliations", institutions) : null,
            orcid != null ? new XElement(ns + "ORCID", "https://orcid.org/" + orcid) : null);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static JsonElement Object(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
