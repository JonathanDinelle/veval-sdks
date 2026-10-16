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

    internal bool ThrowOnSendFailure => _throwOnSendFailure;

    /// <summary>
    /// Returns null only when the trace doesn't exist (404); any other failure throws
    /// <see cref="VevalRequestException"/>, so a wrong API key or a network error can't look like a missing trace.
    /// </summary>
    public Task<TraceData?> GetTraceAsync(string traceId) =>
        GetJsonAsync<TraceData>($"Loading trace '{traceId}'", $"{_endpoint}/v1/traces/{traceId}", nullOn404: true);

    /// <summary>
    /// Loads a scenario's items. Throws <see cref="VevalRequestException"/> on failure — returning an empty
    /// list would run zero items and report the scenario as passed.
    /// </summary>
    internal async Task<List<ScenarioItemDto>> GetScenarioItemsAsync(string scenarioName)
    {
        var result = await GetJsonAsync<ScenarioItemsResponse>(
            $"Loading items for scenario '{scenarioName}'",
            $"{_endpoint}/v1/scenarios/{Uri.EscapeDataString(scenarioName)}/items");
        return result?.Items ?? new List<ScenarioItemDto>();
    }

    private async Task<T?> GetJsonAsync<T>(string what, string url, bool nullOn404 = false) where T : class
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new VevalRequestException($"Veval: {what} failed: {ex.Message}", null, ex);
        }

        using (response)
        {
            if (nullOn404 && response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
                throw new VevalRequestException($"Veval: {what} failed with status {status}{await ReasonAsync(response)}", status);

            return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>The response body as ": body", cut to 500 characters, or "" when it's empty.</summary>
    private static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 500) body = body[..500];
        return body.Length > 0 ? $": {body}" : "";
    }

    /// <summary>Same failure behaviour as <see cref="SendTraceAsync"/>.</summary>
    internal Task PostScenarioRunAsync(string scenarioName, object runPayload) =>
        SendAsync(
            $"Posting scenario run '{scenarioName}'",
            $"{_endpoint}/v1/scenarios/{Uri.EscapeDataString(scenarioName)}/runs",
            runPayload, null);

    private async Task<bool> SendAsync(string what, string url, object payload, bool? throwOnFailure)
    {
        VevalSendException error;
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(url, content);
            if (response.IsSuccessStatusCode) return true;

            var status = (int)response.StatusCode;
            error = new VevalSendException($"Veval: {what} failed with status {status}{await ReasonAsync(response)}", status);
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
