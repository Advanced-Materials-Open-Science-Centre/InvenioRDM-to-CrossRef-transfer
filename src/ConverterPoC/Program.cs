using ConverterPoC;

// Exit code: 0 all deposited, 1 a record failed, 2 some deposits not confirmed by Crossref yet
try
{
    var config = Config.Load("config.json");
    var settings = config.GetConversionSettings();

    Console.WriteLine("Invenio RDM URL: " + config.ApiUrl);
    Console.WriteLine("CrossRef API URL: " + config.CrossRefApiUrl);
    Console.WriteLine("Depositor: " + settings.Depositor.Name + " <" + settings.Depositor.Email + ">");

    var workflow = new DepositWorkflow(
        settings,
        config.ApiUrl,
        new InvenioRDMClient(config.ApiUrl),
        new CrossrefApiClient(config.CrossRefUser, config.CrossRefPassword, config.CrossRefApiUrl),
        Console.Out,
        Directory.GetCurrentDirectory());

    var summary = await workflow.RunAsync(config.DoiMappings ?? [], TimeSpan.FromMinutes(config.ResultTimeoutMinutes));

    return summary.ExitCode;
}
catch (Exception ex)
{
    Console.WriteLine("Exception: " + ex.Message);
    return 1;
}
