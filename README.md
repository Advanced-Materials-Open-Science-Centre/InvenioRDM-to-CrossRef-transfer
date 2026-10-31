## Invenio RDM to CrossRef Transfer tool

InvenioRDM Harvester can perform fully automatic transfer of Invenio RDM (https://inveniordm.web.cern.ch/) publications to CrossRef (https://www.crossref.org/) infrastructure. 

The automated transfer process involves the following key stages:

1.  **Data Retrieval:** Extraction of publication metadata from Invenio RDM in JSON format, utilizing a specified record identifier.
2.  **CrossRef XML Generation:** Transformation of the retrieved JSON data into a valid CrossRef XML document adhering to the CrossRef schema.
3.  **Submission:** Secure upload of the generated XML document to the designated CrossRef XML submission endpoint.

**Note:** This project is currently in its early stages of development, and while functional, users may encounter issues during the XML generation phase. Contributions and feedback are welcome.

### Prerequisites

* .NET SDK 10 or later. Installation instructions can be found at: [https://dotnet.microsoft.com/download](https://dotnet.microsoft.com/download)

### Build Instructions

1.  Navigate to the source directory:
    ```bash
    cd src
    ```
2.  Build the project using the .NET CLI:
    ```bash
    dotnet build
    ```

### Configuration

The application reads `config.json` from its working directory. `config.json` is tracked by git and only holds defaults, so put credentials and the records to deposit in `config.local.json` next to it: it is ignored by git, copied to the build output, and any setting in it overrides `config.json`.

```json
{
  "ApiUrl": "https://dataset.cnu.edu.ua/",
  "AccessToken": "",
  "CrossRefUser": "",
  "CrossRefPassword": "",
  "CrossRefApiUrl": "https://test.crossref.org/servlet/deposit",
  "DepositorName": "",
  "DepositorEmail": "",
  "Registrant": "",
  "RepositoryName": "Openlab Dataset",
  "ResultTimeoutMinutes": 5,
  "DoiMappings": [
    { "Doi": "10.xxxxx/example", "DepositoryRecordId": "abcde-12345" }
  ]
}
```

- `ApiUrl` - Invenio RDM host address
- `AccessToken` - Personal access token generated in Invenio RDM user's cabinet
- `CrossRefUser` - CrossReference account username
- `CrossRefPassword` - CrossReference account password
- `CrossRefApiUrl` - CrossReference XML submission endpoint (`https://doi.crossref.org/servlet/deposit` for production)
- `DepositorName` - Name of the organization or person submitting the deposit
- `DepositorEmail` - E-mail address CrossRef sends deposit success and error reports to
- `Registrant` - Organization that owns the registered content
- `RepositoryName` - Name of the Invenio RDM repository: the title of the CrossRef database datasets are registered in, and the default `publisher` of records (so not a journal title)
- `ResultTimeoutMinutes` - How long to wait for CrossRef to process the deposits (`0` to skip waiting)
- `DoiMappings` - The DOI to register for each Invenio RDM record id


### Run

```bash
.\ConverterPoC.exe
```

For each entry in `DoiMappings` the tool:

1. Loads the record from Invenio RDM and saves it as `<record id>.json`.
2. Checks the DOI: the record must not already list a different DOI under the same prefix, and the DOI must not be registered in CrossRef for another record. DOIs from other prefixes (e.g. Zenodo) only produce a warning.
3. Converts the record to `<record id>.xml` and validates it against the CrossRef 5.5.0 schema. The schema files are downloaded from https://www.crossref.org/schemas/ the first time they are needed and cached in a `Schemas` folder next to the executable; delete that folder to download them again.
4. Uploads it to CrossRef under a unique file name.

A record that fails any step is reported and skipped; if CrossRef rejects the credentials, the remaining records are not processed. Requests that only read (Invenio RDM records, CrossRef lookups and deposit results) are retried on temporary errors; uploads are not. The tool then waits up to `ResultTimeoutMinutes` for CrossRef's deposit results and prints them.

Exit codes: `0` all records deposited, `1` a record failed, `2` nothing failed but CrossRef hasn't confirmed every deposit yet.

### CrossRef content types

| Invenio RDM resource type | CrossRef content type |
|---|---|
| Book (`publication-book`) | Book (monograph) |
| Journal article (`publication-article`) | Journal article; the journal comes from the record's Journal field, or its `publisher` when that isn't `RepositoryName`. Without a journal it is deposited as posted content. |
| Dataset (`dataset`) | Dataset in the `RepositoryName` database |
| Anything else | Posted content (report) |

A DOI that is already registered keeps its CrossRef content type, because CrossRef doesn't allow a deposit to change it.

Besides titles, creators, dates, abstracts and references, each deposit carries:

- the record's first language as the metadata language
- license URLs from the record's rights
- funding: the funder's ROR id (or name) and award number
- related identifiers whose relation type has a CrossRef equivalent
- the subtitle; alternative and translated titles on journal articles only (other CrossRef types have no place for them)
- additional descriptions of type "abstract" as extra abstracts in their language

### Tests

```bash
cd src
dotnet test --filter "Category!=Integration"
```

Unit tests run offline once the CrossRef schema files are cached (the first run downloads them). Integration tests (`Category=Integration`) fetch real records from https://dataset.cnu.edu.ua/ and validate the converted XML with CrossRef's online schema parser, which only checks the XML and never deposits it. CI runs both on every pull request.

### New record check

The [New records](.github/workflows/new-records.yml) workflow runs every day at 19:00 Kyiv time and looks for records published on https://dataset.cnu.edu.ua/ since its last successful run, so a delayed or failed run loses nothing. A new version of a record counts as a new record. If it finds any, it opens an issue listing them; the assignee is set in the workflow, because an assignment is what sends the notification. It only reports: nothing is deposited to CrossRef. To check right away, use **Run workflow** on the workflow's page in the Actions tab.

GitHub disables scheduled workflows in public repositories after 60 days without repository activity. If the issues seem to have stopped, check for a disabled notice on the workflow's page and enable it there: the next run reports everything published in the meantime.
