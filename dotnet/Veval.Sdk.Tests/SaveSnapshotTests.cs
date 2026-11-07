using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Veval.Sdk.Tests;

/// <summary>
/// A saved baseline is pinned unless the caller opts out, and a save the API refuses throws.
/// Mirrors sdk/node/test/save-snapshot.test.ts and sdk/python/tests/test_save_snapshot.py.
/// </summary>
public class SaveSnapshotTests : IDisposable
{
    private const string PinLimitBody = "{\"error\":\"pinned snapshot limit reached (3 of 3 on the free plan) — unpin one or upgrade\"}";

    private readonly TcpListener _api = new(IPAddress.Loopback, 0);
    private readonly List<JsonElement> _bodies = new();

    // What the fake API answers to every request.
    private string _status = "200 OK";
    private string _responseBody = "{\"name\":\"baseline\",\"steps\":[]}";

    public SaveSnapshotTests()
    {
        _api.Start();
        _ = AnswerAsync();
    }

    private VevalSdk Sdk() => new(new VevalOptions
    {
        ApiKey = "key",
        Endpoint = $"http://127.0.0.1:{((IPEndPoint)_api.LocalEndpoint).Port}",
    });

    private async Task AnswerAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _api.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var body = await ReadBodyAsync(stream);
                if (body.Length > 0) lock (_bodies) _bodies.Add(JsonDocument.Parse(body).RootElement.Clone());

                var payload = Encoding.UTF8.GetBytes(_responseBody);
                var head = $"HTTP/1.1 {_status}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(payload);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            // the listener was stopped at the end of the test
        }
    }

    private static async Task<byte[]> ReadBodyAsync(NetworkStream stream)
    {
        var headers = new List<byte>();
        var one = new byte[1];
        while (!(headers.Count >= 4 && headers[^4] == '\r' && headers[^3] == '\n' && headers[^2] == '\r' && headers[^1] == '\n')
               && await stream.ReadAsync(one) == 1)
            headers.Add(one[0]);

        var lengthLine = Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n")
            .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var body = new byte[lengthLine == null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim())];
        var read = 0;
        while (read < body.Length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read));
            if (n == 0) break;
            read += n;
        }
        return body;
    }

    public void Dispose() => _api.Stop();

    [Fact]
    public async Task SavingFromATrace_PinsTheBaselineByDefault()
    {
        await Sdk().SaveSnapshotAsync("baseline", "tr_1");

        var body = _bodies.Should().ContainSingle().Subject;
        body.GetProperty("name").GetString().Should().Be("baseline");
        body.GetProperty("trace_id").GetString().Should().Be("tr_1");
        body.GetProperty("pinned").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SavingFromARun_PinsTheBaselineByDefault()
    {
        var sdk = Sdk();
        VevalExecutionContext? run = null;
        await sdk.RunAsync("agent", ctx => { run = ctx; return Task.FromResult("answer"); });
        _bodies.Clear(); // the trace itself

        await sdk.SaveSnapshotAsync("baseline", run!);

        var body = _bodies.Should().ContainSingle().Subject;
        body.GetProperty("source_trace_id").GetString().Should().Be(run!.TraceId);
        body.GetProperty("pinned").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PinnedFalse_SavesAnUnpinnedBaseline()
    {
        await Sdk().SaveSnapshotAsync("baseline", "tr_1", new SaveSnapshotOptions { Pinned = false });

        _bodies.Should().ContainSingle().Which.GetProperty("pinned").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AtThePinLimit_ThrowsWithTheLimitAndHowToSaveUnpinned()
    {
        _status = "409 Conflict";
        _responseBody = PinLimitBody;

        var act = () => Sdk().SaveSnapshotAsync("baseline", "tr_1");

        var thrown = await act.Should().ThrowAsync<VevalRequestException>()
            .WithMessage("*409*pinned snapshot limit reached (3 of 3 on the free plan)*Pinned = false*");
        thrown.Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task ASaveTheApiRejects_ThrowsWithItsStatus()
    {
        _status = "404 Not Found";
        _responseBody = "{\"error\":\"trace 'tr_1' not found\"}";

        var act = () => Sdk().SaveSnapshotAsync("baseline", "tr_1");

        var thrown = await act.Should().ThrowAsync<VevalRequestException>().WithMessage("*404*not found*");
        thrown.Which.StatusCode.Should().Be(404);
        thrown.Which.Message.Should().NotContain("Pinned = false");
    }

    [Fact]
    public async Task ANetworkFailure_ThrowsWithoutAStatus()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "key", Endpoint = "http://127.0.0.1:1" });

        var act = () => sdk.SaveSnapshotAsync("baseline", "tr_1");

        (await act.Should().ThrowAsync<VevalRequestException>()).Which.StatusCode.Should().BeNull();
    }
}
