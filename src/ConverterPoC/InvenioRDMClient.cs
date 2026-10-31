using System.Net.Http.Headers;
using System.Text;

namespace ConverterPoC;

public class InvenioRDMClient
{
    private readonly string _apiUrl;
    private readonly HttpClient _client;
    private readonly TimeSpan _retryDelay;

    public InvenioRDMClient(string apiUrl, HttpClient? httpClient = null, TimeSpan? retryDelay = null)
    {
        _apiUrl = apiUrl;
        _client = httpClient ?? new HttpClient();
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    // Always fetches the current record: a local copy would go stale after edits in InvenioRDM
    public async Task<string?> LoadRecordAsync(string recordId)
    {
        using var response = await HttpRetry.SendAsync(_client, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiUrl}api/records/{recordId}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return request;
        }, _retryDelay);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
            return null;
        }

        return Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
    }
}
