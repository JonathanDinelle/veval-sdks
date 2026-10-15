using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Veval.Sdk.Assertions;

namespace Veval.Sdk.Internal;

internal class VevalHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly bool _throwOnSendFailure;
    private readonly Action<string> _warn;

    public VevalHttpClient(VevalOptions options)
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        _endpoint = options.Endpoint.TrimEnd('/');
        _throwOnSendFailure = options.ThrowOnSendFailure;
        _warn = options.Warn;
    }

    /// <summary>
    /// Never throws unless <paramref name="throwOnFailure"/> (default: the ThrowOnSendFailure option) is set;
    /// a failed send is logged as a warning instead, so tracing can't break the host app.
    /// Returns whether the trace was stored.
    /// </summary>
    public Task<bool> SendTraceAsync(object tracePayload, bool? throwOnFailure = null) =>
        SendAsync("Sending trace", $"{_endpoint}/v1/traces", tracePayload, throwOnFailure);

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

    /// <summary>Same failure behaviour as <see cref="SendTraceAsync"/>.</summary>
    internal Task PostScenarioRunAsync(string scenarioName, object runPayload) =>
        SendAsync(
            $"Posting scenario run '{scenarioName}'",
            $"{_endpoint}/v1/scenarios/{Uri.EscapeDataString(scenarioName)}/runs",
            runPayload, null);

    /// <summary>
    /// Scenario item traces are sent without throwing, so one failed send can't abort the remaining items;
    /// in strict mode the scenario throws once, after its run is posted.
    /// </summary>
    internal void ThrowIfUndelivered(string scenarioName, int undelivered)
    {
        if (undelivered > 0 && _throwOnSendFailure)
            throw new VevalSendException(
                $"Veval: {undelivered} trace(s) from scenario '{scenarioName}' could not be stored; see the warnings above",
                null);
    }

    private async Task<bool> SendAsync(string what, string url, object payload, bool? throwOnFailure)
    {
        VevalSendException error;
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content);
            if (response.IsSuccessStatusCode) return true;

            var body = await response.Content.ReadAsStringAsync();
            if (body.Length > 500) body = body[..500];
            var status = (int)response.StatusCode;
            var reason = body.Length > 0 ? $": {body}" : "";
            error = new VevalSendException($"Veval: {what} failed with status {status}{reason}", status);
        }
        catch (Exception ex)
        {
            error = new VevalSendException($"Veval: {what} failed: {ex.Message}", null, ex);
        }

        _warn(error.Message);
        if (throwOnFailure ?? _throwOnSendFailure) throw error;
        return false;
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
