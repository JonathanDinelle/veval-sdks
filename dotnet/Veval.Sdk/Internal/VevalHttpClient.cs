using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Veval.Sdk.Internal;

internal class VevalHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;

    public VevalHttpClient(string apiKey, string endpoint)
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _endpoint = endpoint.TrimEnd('/');
    }

    public async Task SendTraceAsync(object tracePayload)
    {
        try
        {
            var json = JsonSerializer.Serialize(tracePayload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PostAsync($"{_endpoint}/v1/traces", content);
        }
        catch
        {
            // SDK must never throw on flush failure
        }
    }

    public async Task<TraceData?> GetTraceAsync(string traceId)
    {
        try
        {
            var response = await _http.GetAsync($"{_endpoint}/v1/traces/{traceId}");
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<TraceData>(json);
        }
        catch
        {
            return null;
        }
    }

    internal async Task<List<ScenarioItemDto>> GetScenarioItemsAsync(string scenarioName)
    {
        try
        {
            var response = await _http.GetAsync($"{_endpoint}/v1/scenarios/{Uri.EscapeDataString(scenarioName)}/items");
            if (!response.IsSuccessStatusCode) return new List<ScenarioItemDto>();
            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<ScenarioItemsResponse>(json);
            return result?.Items ?? new List<ScenarioItemDto>();
        }
        catch
        {
            return new List<ScenarioItemDto>();
        }
    }

    internal async Task PostScenarioRunAsync(string scenarioName, object runPayload)
    {
        try
        {
            var json = JsonSerializer.Serialize(runPayload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PostAsync($"{_endpoint}/v1/scenarios/{Uri.EscapeDataString(scenarioName)}/runs", content);
        }
        catch
        {
            // fire-and-forget — never throw
        }
    }

    public void Dispose() => _http.Dispose();
}
