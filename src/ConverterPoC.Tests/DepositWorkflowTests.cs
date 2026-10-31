using System.Net;

namespace ConverterPoC.Tests;

// The whole deposit run against in-memory InvenioRDM and Crossref
public sealed class DepositWorkflowTests : IDisposable
{
    private const string RepositoryUrl = "https://repository.example/";
    private const string DepositUrl = "https://crossref.example/servlet/deposit";

    private const string SuccessLog = """
        <doi_batch_diagnostic status="completed"><record_diagnostic status="Success"><doi>x</doi><msg>Successfully added</msg></record_diagnostic>
        <batch_data><record_count>1</record_count><success_count>1</success_count><warning_count>0</warning_count><failure_count>0</failure_count></batch_data></doi_batch_diagnostic>
        """;

    private const string FailureLog = """
        <doi_batch_diagnostic status="completed"><record_diagnostic status="Failure"><doi>x</doi><msg>Rejected by test</msg></record_diagnostic>
        <batch_data><record_count>1</record_count><success_count>0</success_count><warning_count>0</warning_count><failure_count>1</failure_count></batch_data></doi_batch_diagnostic>
        """;

    private const string QueuedLog = """<doi_batch_diagnostic status="queued" />""";

    private readonly FakeHttp _http = new();
    private readonly StringWriter _log = new();
    private readonly string _output = Directory.CreateTempSubdirectory("workflow-tests-").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public async Task NewDoi_IsConvertedValidatedUploadedAndConfirmed()
    {
        Record("rec01-00001", TestRecords.Json(resourceType: "dataset"));
        AcceptUploads();
        Results("rec01-00001", QueuedLog, SuccessLog);

        var summary = await Run(Map("10.15330/test.01", "rec01-00001"));

        Assert.Equal(0, summary.ExitCode);
        Assert.Equal(["rec01-00001"], summary.Deposited);

        var upload = Assert.Single(_http.To(DepositUrl));
        Assert.Contains("rec01-00001_", upload.Body);
        Assert.Contains("<database>", upload.Body);
        Assert.True(File.Exists(Path.Combine(_output, "rec01-00001.xml")));
        Assert.True(File.Exists(Path.Combine(_output, "rec01-00001.json")));
        Assert.Contains("Record rec01-00001: Success", _log.ToString());
        Assert.Contains("1 of 1 record(s) deposited", _log.ToString());
    }

    // e.g. the datasets already registered as posted content
    [Fact]
    public async Task RegisteredDoi_KeepsItsContentType()
    {
        Record("rec01-00001", TestRecords.Json(resourceType: "dataset"));
        Registered("10.15330/test.01", "posted-content", RepositoryUrl + "records/rec01-00001");
        AcceptUploads();
        Results("rec01-00001", SuccessLog);

        var summary = await Run(Map("10.15330/test.01", "rec01-00001"));

        Assert.Equal(0, summary.ExitCode);
        Assert.Contains("<posted_content", Assert.Single(_http.To(DepositUrl)).Body);
    }

    [Fact]
    public async Task DoiRegisteredForAnotherRecord_IsNotUploaded()
    {
        Record("rec01-00001", TestRecords.Json());
        Registered("10.15330/test.01", "monograph", RepositoryUrl + "records/other-00002");

        var summary = await Run(Map("10.15330/test.01", "rec01-00001"));

        Assert.Equal(["rec01-00001"], summary.Failed);
        Assert.Empty(_http.To(DepositUrl));
        Assert.Contains("already registered for", _log.ToString());
    }

    [Fact]
    public async Task RecordListingAnotherDoiUnderTheSamePrefix_IsNotUploaded()
    {
        Record("rec01-00001", TestRecords.Json(identifiers: """[{ "identifier": "10.15330/other.01", "scheme": "doi" }]"""));

        var summary = await Run(Map("10.15330/test.01", "rec01-00001"));

        Assert.Equal(["rec01-00001"], summary.Failed);
        Assert.Empty(_http.To(DepositUrl));
    }

    [Fact]
    public async Task FailedRecord_DoesNotStopTheBatch()
    {
        Record("good0-00002", TestRecords.Json());
        AcceptUploads();
        Results("good0-00002", SuccessLog);

        var summary = await Run(Map("10.15330/test.01", "gone0-00001"), Map("", "nodoi-00003"), Map("10.15330/test.02", "good0-00002"));

        Assert.Equal(1, summary.ExitCode);
        Assert.Equal(["gone0-00001", "nodoi-00003"], summary.Failed);
        Assert.Equal(["good0-00002"], summary.Deposited);
        Assert.Contains("Could not load the record", _log.ToString());
        Assert.Contains("has no Doi", _log.ToString());
    }

    [Fact]
    public async Task FailureInCrossrefDepositLog_FailsTheRecord()
    {
        Record("rec01-00001", TestRecords.Json());
        AcceptUploads();
        Results("rec01-00001", FailureLog);

        var summary = await Run(Map("10.15330/test.01", "rec01-00001"));

        Assert.Equal(1, summary.ExitCode);
        Assert.Equal(["rec01-00001"], summary.Failed);
        Assert.Contains("Rejected by test", _log.ToString());
    }

    [Fact]
    public async Task DepositStillQueuedAtTimeout_IsUnconfirmed_WithExitCode2()
    {
        Record("rec01-00001", TestRecords.Json());
        AcceptUploads();
        Results("rec01-00001", QueuedLog);

        var summary = await Run([Map("10.15330/test.01", "rec01-00001")], TimeSpan.FromSeconds(30));

        Assert.Equal(2, summary.ExitCode);
        Assert.Equal(["rec01-00001"], summary.Unconfirmed);
        Assert.Equal(2, _http.To("submissionDownload").Count());
        Assert.Contains("not confirmed yet: rec01-00001", _log.ToString());
    }

    [Fact]
    public async Task WrongCredentials_StopTheBatchAndShowCrossrefsMessage()
    {
        Record("rec01-00001", TestRecords.Json());
        Record("rec02-00002", TestRecords.Json());
        _http.On((r, _) => r.RequestUri!.AbsoluteUri == DepositUrl, FakeHttp.Respond(HttpStatusCode.Unauthorized,
            "<html><body><h2>FAILURE</h2><p>Your XML batch submission failed. [Wrong credentials. Incorrect username or password.]</p></body></html>"));

        var summary = await Run(Map("10.15330/test.01", "rec01-00001"), Map("10.15330/test.02", "rec02-00002"));

        Assert.Equal(["rec01-00001", "rec02-00002"], summary.Failed);
        Assert.Single(_http.To(DepositUrl));
        Assert.Empty(_http.To("api/records/rec02-00002"));
        Assert.Contains("Wrong credentials. Incorrect username or password.", _log.ToString());
        Assert.Contains("not processed: rec02-00002", _log.ToString());
    }

    [Fact]
    public async Task TransientErrorsOnReads_AreRetried()
    {
        _http.On((r, _) => r.RequestUri!.AbsoluteUri.EndsWith("api/records/rec01-00001"),
            FakeHttp.Respond(HttpStatusCode.ServiceUnavailable),
            FakeHttp.Fail(new HttpRequestException("connection reset")),
            FakeHttp.Respond(HttpStatusCode.OK, TestRecords.Json()));
        AcceptUploads();
        Results("rec01-00001", SuccessLog);

        var summary = await Run(Map("10.15330/test.01", "rec01-00001"));

        Assert.Equal(0, summary.ExitCode);
        Assert.Equal(3, _http.To("api/records/rec01-00001").Count());
    }

    [Fact]
    public async Task ServerErrorOnUpload_IsNotRetried()
    {
        Record("rec01-00001", TestRecords.Json());
        _http.On((r, _) => r.RequestUri!.AbsoluteUri == DepositUrl, FakeHttp.Respond(HttpStatusCode.ServiceUnavailable, "Service down"));

        var summary = await Run(Map("10.15330/test.01", "rec01-00001"));

        Assert.Equal(["rec01-00001"], summary.Failed);
        Assert.Single(_http.To(DepositUrl));
        Assert.Contains("503", _log.ToString());
        Assert.Contains("Service down", _log.ToString());
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(0, 1, 2)]
    public void ExitCode_FailuresBeforeUnconfirmed(int failed, int unconfirmed, int expected)
    {
        var summary = new DepositSummary([], Enumerable.Repeat("x", failed).ToList(), Enumerable.Repeat("y", unconfirmed).ToList());

        Assert.Equal(expected, summary.ExitCode);
    }

    private Task<DepositSummary> Run(params DoiMapping[] mappings) => Run(mappings, TimeSpan.FromMinutes(1));

    private Task<DepositSummary> Run(DoiMapping[] mappings, TimeSpan resultTimeout)
    {
        var workflow = new DepositWorkflow(
            TestRecords.Settings,
            RepositoryUrl,
            new InvenioRDMClient(RepositoryUrl, new HttpClient(_http), TimeSpan.Zero),
            new CrossrefApiClient("user", "password", DepositUrl, new HttpClient(_http), TimeSpan.Zero),
            _log,
            _output,
            _ => Task.CompletedTask);

        return workflow.RunAsync(mappings, resultTimeout);
    }

    private static DoiMapping Map(string doi, string recordId) => new() { Doi = doi, DepositoryRecordId = recordId };

    private void Record(string id, string json) =>
        _http.On((r, _) => r.RequestUri!.AbsoluteUri == $"{RepositoryUrl}api/records/{id}", FakeHttp.Respond(HttpStatusCode.OK, json));

    private void Registered(string doi, string type, string url) =>
        _http.On((r, _) => r.RequestUri!.Host == "api.crossref.org" && Uri.UnescapeDataString(r.RequestUri.AbsoluteUri).EndsWith(doi),
            FakeHttp.Respond(HttpStatusCode.OK, $$"""{ "message": { "DOI": "{{doi}}", "type": "{{type}}", "resource": { "primary": { "URL": "{{url}}" } } } }"""));

    private void AcceptUploads() =>
        _http.On((r, _) => r.RequestUri!.AbsoluteUri == DepositUrl,
            FakeHttp.Respond(HttpStatusCode.OK, "<html><body><h2>SUCCESS</h2></body></html>"));

    private void Results(string recordId, params string[] logs) =>
        _http.On((r, body) => r.RequestUri!.AbsolutePath.EndsWith("submissionDownload") && body.Contains(recordId),
            logs.Select(log => FakeHttp.Respond(HttpStatusCode.OK, log)).ToArray());
}
