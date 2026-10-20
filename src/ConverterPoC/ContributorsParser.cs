using System.Text.Json;
using System.Xml.Linq;

namespace ConverterPoC;

// Creators become authors; contributors are included when their role has a Crossref equivalent.
// Throws on entries it can't read, so a record never silently loses its authors.
public class ContributorsParser
{
    // InvenioRDM contributor role id -> Crossref contributor_role. Crossref has no data roles, so
    // data curators map to the closest one (editor) and data collectors to author; the rest
    // (datamanager, contactperson, ...) are skipped.
    private static readonly Dictionary<string, string> CrossrefRoles = new()
    {
        ["editor"] = "editor",
        ["datacurator"] = "editor",
        ["datacollector"] = "author",
    };

    public static XElement ConvertContributorsToXml(XNamespace nameSpace, JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metadata))
            throw new InvalidOperationException("No metadata found in the InvenioRDM JSON");

        // Creators are the authors of the work regardless of their InvenioRDM role
        var entries = Array(metadata, "creators").Select(c => (Contributor: c, Role: "author")).ToList();

        if (entries.Count == 0)
            throw new InvalidOperationException("Record has no creators");

        var skippedRoles = new List<string>();

        foreach (var contributor in Array(metadata, "contributors"))
        {
            var role = String(Object(contributor, "role"), "id") ?? "";

            if (CrossrefRoles.TryGetValue(role, out var crossrefRole))
                entries.Add((contributor, crossrefRole));
            else
                skippedRoles.Add(role == "" ? "(none)" : role);
        }

        if (skippedRoles.Count > 0)
        {
            Console.WriteLine($"Skipped {skippedRoles.Count} contributor(s) with roles that have no Crossref equivalent: " +
                              string.Join(", ", skippedRoles.Distinct()));
        }

        return new XElement(nameSpace + "contributors",
            entries.Select((entry, index) =>
                ToXml(nameSpace, entry.Contributor, entry.Role, index == 0 ? "first" : "additional", index + 1)));
    }

    private static XElement ToXml(XNamespace ns, JsonElement contributor, string role, string sequence, int position)
    {
        var personOrOrg = Object(contributor, "person_or_org");
        var type = String(personOrOrg, "type");

        if (type == "organizational")
        {
            var name = String(personOrOrg, "name");

            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException($"Creator/contributor {position} is an organization without a name");

            return new XElement(ns + "organization",
                new XAttribute("sequence", sequence),
                new XAttribute("contributor_role", role),
                name);
        }

        if (type != "personal")
            throw new InvalidOperationException($"Creator/contributor {position} has unknown person_or_org type '{type}'");

        var givenName = String(personOrOrg, "given_name");
        var familyName = String(personOrOrg, "family_name");

        if (string.IsNullOrWhiteSpace(givenName) && string.IsNullOrWhiteSpace(familyName))
            throw new InvalidOperationException($"Creator/contributor {position} has no name");

        var institutions = Array(contributor, "affiliations")
            .Select(affiliation => String(affiliation, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new XElement(ns + "institution", new XElement(ns + "institution_name", name)))
            .ToList();

        // Crossref takes a single ORCID per person
        var orcid = Array(personOrOrg, "identifiers")
            .Where(identifier => string.Equals(String(identifier, "scheme"), "orcid", StringComparison.OrdinalIgnoreCase))
            .Select(identifier => String(identifier, "identifier"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return new XElement(ns + "person_name",
            new XAttribute("sequence", sequence),
            new XAttribute("contributor_role", role),
            string.IsNullOrWhiteSpace(givenName) ? null : new XElement(ns + "given_name", givenName),
            // Crossref requires a surname; a person with only a given name is deposited under it
            new XElement(ns + "surname", string.IsNullOrWhiteSpace(familyName) ? givenName : familyName),
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
