using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Veval.Sdk.Assertions;

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

    /// <summary>
    /// Unlike the other calls on this client, JudgeAsync deliberately does not swallow
    /// failures — a network/API error here must surface as an assertion failure, not a
    /// silent pass. Callers are expected to catch.
    /// </summary>
    internal async Task<JudgeResult> JudgeAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{_endpoint}/v1/judge", content);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Judge request failed with status {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var responseJson = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JudgeResult>(responseJson)
            ?? throw new InvalidOperationException("Judge response could not be parsed.");
    }

    /// <summary>
    /// Saves a named snapshot baseline. Throws on failure — a baseline that silently didn't save would make
    /// every later comparison fail (or worse, compare against a stale one).
    /// </summary>
    internal async Task<SnapshotData> CreateSnapshotAsync(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync($"{_endpoint}/v1/snapshots", content);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Saving snapshot failed with status {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return JsonSerializer.Deserialize<SnapshotData>(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Snapshot response could not be parsed.");
    }

    /// <summary>
    /// Loads the latest baseline with this name. Returns null only when none exists (404); any other
    /// failure throws, so a network error can never look like "nothing to compare against".
    /// </summary>
    internal async Task<SnapshotData?> GetSnapshotAsync(string name)
    {
        var response = await _http.GetAsync($"{_endpoint}/v1/snapshots/{Uri.EscapeDataString(name)}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Loading snapshot '{name}' failed with status {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return JsonSerializer.Deserialize<SnapshotData>(await response.Content.ReadAsStringAsync());
    }

    public void Dispose() => _http.Dispose();
}
