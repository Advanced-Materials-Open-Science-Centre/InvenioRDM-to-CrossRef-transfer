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

1. Loads the record from Invenio RDM and saves it as `<record id>.json`. Records are read anonymously, so only public ones can be deposited.
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

### Automatic deposits

The [New records](.github/workflows/new-records.yml) workflow runs every day at 19:17 Kyiv time (started by a Cloudflare Worker, see [Schedule](#schedule)) and deposits new records from https://dataset.cnu.edu.ua/ to the production CrossRef system, where a registered DOI is permanent. To run it right away, use **Run workflow** on the workflow's page in the Actions tab. It needs these repository secrets: `CROSSREF_USER`, `CROSSREF_PASSWORD`, `DEPOSITOR_NAME`, `DEPOSITOR_EMAIL` and `REGISTRANT`.

[`doi.json`](doi.json) maps each DOI the workflow deposited to its record id:

```json
{
  "10.15330/dataset.26.10.02": "dhd7p-6fh43"
}
```

A record created on or after 2026-10-27 is new until `doi.json` lists it. A new version of a record is a record of its own, and adding a record to the file by hand keeps it from being deposited. Older records were deposited by hand and are ignored, so they aren't in the file.

For each new record, oldest first, the workflow:

1. Picks the next DOI, `10.15330/dataset.YY.MM.NN`: the current year and month in Kyiv, and the number after the highest one `doi.json` has for that month.
2. Runs ConverterPoC with that DOI mapping.
3. When the deposit succeeds, adds the pair to `doi.json` and pushes it.

A deposit CrossRef hasn't confirmed within 30 minutes (exit code `2`) is added too, because depositing it again could give the record a second DOI. If CrossRef rejects it later, remove the pair from `doi.json` and the next run deposits the record again. A failed record isn't added, so the next run tries it again, and the run is marked as failed.

The workflow then opens an issue with the result for each record. The assignee is set in the workflow, because an assignment is what sends the notification.

#### Schedule

GitHub starts scheduled workflows hours late, so the workflow has no schedule of its own. The [cron-trigger](cron-trigger) Worker starts it instead with a [Cloudflare Cron Trigger](https://developers.cloudflare.com/workers/configuration/cron-triggers/). Cron Triggers run on UTC, so the Worker runs at 16:17 and 17:17 UTC and only starts the workflow when it is 19:17 in Kyiv.

The Worker needs a fine-grained GitHub token for this repository with the **Actions: Read and write** permission. To deploy it:

```bash
cd cron-trigger
npx wrangler login
npx wrangler secret put GITHUB_TOKEN
npx wrangler deploy
```

Run `npx wrangler deploy` again after changing [`wrangler.jsonc`](cron-trigger/wrangler.jsonc) or the Worker's code. A run the Worker couldn't start shows up as an error in the Worker's logs in the Cloudflare dashboard. Nothing is lost if it fails: the next run deposits everything published in the meantime. Renew the token before it expires.

#### Telegram notifications

When a run that found new records or failed ends, however it was started, the workflow sends its result to Telegram: the number of new records and what happened to each, or that the run failed, with a link to the run log. A run that finds nothing new and doesn't fail sends nothing. It needs:

- a bot created with [@BotFather](https://t.me/BotFather), whose token is the `TELEGRAM_BOT_TOKEN` repository secret
- the chats to send to in the `TELEGRAM_CHAT_IDS` repository variable, separated by commas. Nothing is sent while it isn't set

A bot can only write to someone who has started a chat with it, so send it a message first. That message also gives you the chat id: open `https://api.telegram.org/bot<token>/getUpdates` and take `message.chat.id`. A private chat's id is the user's id; a group's is negative. Once the [Telegram bot](#telegram-bot) is connected, `/whoami` shows both instead, because the bot then receives the messages and `getUpdates` stays empty.

#### Telegram bot

The cron-trigger Worker is also a Telegram bot that starts the workflow. It is the same bot that sends the notifications, and anyone can use it:

| Command | What it does |
|---|---|
| `/run` | Starts the workflow |
| `/status` | Shows the latest run |
| `/whoami` | Shows your Telegram id and the chat's |

That is harmless, because a run only deposits records that are new, and runs never overlap. Telegram posts each message to the Worker's `/telegram` URL, and the Worker sends its answer back in the response, so it uses its `GITHUB_TOKEN` but no bot token. A request without the webhook's secret token is refused, so only Telegram can post there.

To connect the bot:

1. Pick a secret of up to 256 letters, digits, `_` and `-`, for example from `node -e "console.log(crypto.randomUUID())"`, and give it to the Worker:
   ```bash
   cd cron-trigger
   npx wrangler secret put TELEGRAM_WEBHOOK_SECRET
   npx wrangler deploy
   ```
   `deploy` prints the Worker's URL, `https://new-records-cron-trigger.<subdomain>.workers.dev`.
2. Point the bot at it, with the bot token and the same secret:
   ```bash
   curl https://api.telegram.org/bot<bot token>/setWebhook \
     --data url=https://new-records-cron-trigger.<subdomain>.workers.dev/telegram \
     --data secret_token=<secret> --data 'allowed_updates=["message"]'
   ```
3. Optionally, have Telegram suggest the commands:
   ```bash
   curl https://api.telegram.org/bot<bot token>/setMyCommands --header 'Content-Type: application/json' \
     --data '{"commands":[{"command":"run","description":"Start the New records workflow"},{"command":"status","description":"The latest run of the workflow"},{"command":"whoami","description":"Your Telegram id and the chat'"'"'s"}]}'
   ```

`https://api.telegram.org/bot<bot token>/getWebhookInfo` shows whether Telegram delivers the messages, and the last error if it doesn't. Changing `TELEGRAM_WEBHOOK_SECRET` needs `setWebhook` again with the new one.
