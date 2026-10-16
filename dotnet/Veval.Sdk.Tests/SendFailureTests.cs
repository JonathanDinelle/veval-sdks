using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;

namespace Veval.Sdk.Tests;

/// <summary>
/// A trace or scenario run that can't be delivered is logged by default and thrown in strict mode.
/// Mirrors sdk/node/test/send-failures.test.ts and sdk/python/tests/test_send_failures.py.
/// </summary>
public class SendFailureTests : IDisposable
{
    private readonly TcpListener _unauthorizedApi = new(IPAddress.Loopback, 0);
    private readonly List<string> _warnings = new();

    // Path prefixes the fake API rejects with 401 or answers with 404; everything else gets 200.
    private string[] _rejected = ["/"];
    private string[] _missing = [];

    public SendFailureTests()
    {
        _unauthorizedApi.Start();
        _ = AnswerUnauthorizedAsync();
    }

    private string UnauthorizedEndpoint => $"http://127.0.0.1:{((IPEndPoint)_unauthorizedApi.LocalEndpoint).Port}";

    private VevalOptions Options(string endpoint, bool strict) => new()
    {
        ApiKey = "wrong",
        Endpoint = endpoint,
        ThrowOnSendFailure = strict,
        Warn = message => { lock (_warnings) _warnings.Add(message); },
    };

    private async Task AnswerUnauthorizedAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _unauthorizedApi.AcceptTcpClientAsync();
                var stream = client.GetStream();
                var path = await ReadRequestAsync(stream);
                const string body = "{\"error\":\"invalid api key\"}";
                var response =
                    _missing.Any(path.StartsWith) ? "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                    : _rejected.Any(path.StartsWith)
                        ? "HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\n" +
                          $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"
                        : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
            // the listener was stopped at the end of the test
        }
    }

    /// <summary>Reads one request and returns its path.</summary>
    private static async Task<string> ReadRequestAsync(NetworkStream stream)
    {
        // Read the headers, then exactly Content-Length bytes of body, so the client sees a clean response.
        var buffer = new List<byte>();
        var one = new byte[1];
        while (!EndsWithBlankLine(buffer) && await stream.ReadAsync(one) == 1) buffer.Add(one[0]);

        var headers = Encoding.ASCII.GetString(buffer.ToArray());
        var lengthLine = headers.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var remaining = lengthLine == null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim());
        var body = new byte[4096];
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(body.AsMemory(0, Math.Min(body.Length, remaining)));
            if (read == 0) break;
            remaining -= read;
        }

        return headers.Split(' ').ElementAtOrDefault(1) ?? "/";
    }

    private static bool EndsWithBlankLine(List<byte> b) =>
        b.Count >= 4 && b[^4] == '\r' && b[^3] == '\n' && b[^2] == '\r' && b[^1] == '\n';

    public void Dispose() => _unauthorizedApi.Stop();

    [Fact]
    public async Task RejectedTrace_IsLoggedWithItsStatus_AndTheRunStillSucceeds()
    {
        var sdk = new VevalSdk(Options(UnauthorizedEndpoint, strict: false));

        var result = await sdk.RunAsync("agent", _ => Task.FromResult("answer"));

        result.Should().Be("answer");
        _warnings.Should().ContainSingle().Which.Should().Contain("401").And.Contain("invalid api key");
    }

    [Fact]
    public async Task StrictMode_ThrowsARejectedTrace()
    {
        var sdk = new VevalSdk(Options(UnauthorizedEndpoint, strict: true));

        var act = () => sdk.RunAsync("agent", _ => Task.FromResult("answer"));

        (await act.Should().ThrowAsync<VevalSendException>()).Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task StrictMode_ThrowsANetworkFailure()
    {
        var sdk = new VevalSdk(Options("http://127.0.0.1:1", strict: true));

        var act = () => sdk.RunAsync("agent", _ => Task.FromResult("answer"));

        (await act.Should().ThrowAsync<VevalSendException>()).Which.StatusCode.Should().BeNull();
        _warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task StrictMode_KeepsTheAgentsOwnError()
    {
        var sdk = new VevalSdk(Options(UnauthorizedEndpoint, strict: true));

        var act = () => sdk.RunAsync<string>("agent", _ => throw new InvalidOperationException("agent crashed"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("agent crashed");
        _warnings.Should().ContainSingle().Which.Should().Contain("401");
    }

    [Fact]
    public async Task StrictMode_TestSdkKeepsTheAgentsOwnError_AndStillSendsTheErrorTrace()
    {
        var sdk = new VevalTestSdk(Options(UnauthorizedEndpoint, strict: true));

        var act = () => sdk.RunAsync<string>("agent", _ => throw new InvalidOperationException("agent crashed"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("agent crashed");
        _warnings.Should().ContainSingle().Which.Should().Contain("401");
    }

    [Fact]
    public async Task StrictMode_ThrowsARejectedScenarioRun()
    {
        var sdk = new VevalTestSdk(Options(UnauthorizedEndpoint, strict: true));

        var act = () => sdk.RunScenarioAsync("echo", _ => Task.FromResult("answer"), [],
            [new ScenarioItem { Name = "a", Input = "hi" }]);

        (await act.Should().ThrowAsync<VevalSendException>()).Which.StatusCode.Should().Be(401);
        _warnings.Should().Contain(w => w.Contains("scenario run 'echo'"));
    }

    [Fact]
    public async Task StrictMode_RunsEveryScenarioItemBeforeThrowing()
    {
        var sdk = new VevalSdk(Options(UnauthorizedEndpoint, strict: true));
        var calls = new List<object?>();

        var act = () => sdk.RunScenarioAsync("echo", ctx => { calls.Add(ctx.Input); return Task.FromResult("answer"); }, [],
            new[] { "a", "b", "c" }.Select(n => new ScenarioItem { Name = n, Input = n }).ToArray());

        await act.Should().ThrowAsync<VevalSendException>();
        calls.Should().Equal("a", "b", "c");
        _warnings.Count(w => w.Contains("Sending trace")).Should().Be(3);
        _warnings.Should().Contain(w => w.Contains("scenario run 'echo'"));
    }

    [Fact]
    public async Task StrictMode_ThrowsAfterAPostedScenarioWhoseTracesWereLost()
    {
        _rejected = ["/v1/traces"];
        var sdk = new VevalSdk(Options(UnauthorizedEndpoint, strict: true));

        var act = () => sdk.RunScenarioAsync("echo", _ => Task.FromResult("answer"), [],
            [new ScenarioItem { Name = "a", Input = "hi" }]);

        await act.Should().ThrowAsync<VevalSendException>().WithMessage("*1 trace(s) from scenario 'echo'*");
    }

    [Fact]
    public async Task ScenarioItemsThatCantBeLoaded_Throw_EvenWithoutStrictMode()
    {
        var sdk = new VevalSdk(Options(UnauthorizedEndpoint, strict: false));
        var calls = 0;

        var act = () => sdk.RunScenarioAsync("echo", _ => { calls++; return Task.FromResult("answer"); }, []);

        var thrown = await act.Should().ThrowAsync<VevalRequestException>().WithMessage("*scenario 'echo'*401*invalid api key*");
        thrown.Which.StatusCode.Should().Be(401);
        calls.Should().Be(0);
    }

    [Fact]
    public async Task TracesThatCantBeLoaded_Throw_InsteadOfLookingMissing()
    {
        var sdk = new VevalSdk(Options(UnauthorizedEndpoint, strict: false));

        var act = () => sdk.RunScenarioAsync("echo", _ => Task.FromResult("answer"), [],
            [new ScenarioItem { Name = "a", TraceId = "tr_saved" }]);

        var thrown = await act.Should().ThrowAsync<VevalRequestException>().WithMessage("*trace 'tr_saved'*401*");
        thrown.Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task MissingTrace_FailsOnlyItsItem()
    {
        _rejected = [];
        _missing = ["/v1/traces/"];
        var sdk = new VevalSdk(Options(UnauthorizedEndpoint, strict: true));

        var result = await sdk.RunScenarioAsync("echo", _ => Task.FromResult("answer"), [],
            [new ScenarioItem { Name = "saved", TraceId = "tr_gone" }, new ScenarioItem { Name = "live", Input = "hi" }]);

        result.Results[0].Failures.Should().ContainSingle().Which.Should().Be("trace 'tr_gone' not found");
        result.Results[1].Passed.Should().BeTrue();
        _warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task UnreachableApi_ThrowsWithoutAStatus_WhenLoadingATrace()
    {
        var sdk = new VevalSdk(Options("http://127.0.0.1:1", strict: false));

        var act = () => sdk.GetTraceAsync("tr_saved");

        (await act.Should().ThrowAsync<VevalRequestException>()).Which.StatusCode.Should().BeNull();
    }
}
