"""A saved baseline is pinned unless the caller opts out, and a save the API refuses raises.
Mirrors sdk/node/test/save-snapshot.test.ts and sdk/dotnet/Veval.Sdk.Tests/SaveSnapshotTests.cs."""
import asyncio
import json
import os
import sys
import threading
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, HTTPServer

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from veval import SaveSnapshotOptions, VevalExecutionContext, VevalOptions, VevalRequestError, VevalSdk  # noqa: E402

PIN_LIMIT_BODY = '{"error":"pinned snapshot limit reached (3 of 3 on the free plan) — unpin one or upgrade"}'


class _Api(BaseHTTPRequestHandler):
    # What the fake API answers to every request, and the JSON bodies it received.
    status = 200
    response_body = '{"name":"baseline","steps":[]}'
    received: list = []

    def do_POST(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        if raw:
            self.received.append(json.loads(raw))
        body = self.response_body.encode("utf-8")
        self.send_response(self.status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


@contextmanager
def api(status=200, response_body='{"name":"baseline","steps":[]}'):
    """Yields an SDK pointed at the fake API, and the list of bodies it receives."""
    received: list = []
    handler = type("Handler", (_Api,), {"status": status, "response_body": response_body, "received": received})
    server = HTTPServer(("127.0.0.1", 0), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    previous = os.environ.get("VEVAL_INTERNAL_ENDPOINT")
    os.environ["VEVAL_INTERNAL_ENDPOINT"] = f"http://127.0.0.1:{server.server_port}"
    try:
        yield VevalSdk(VevalOptions(api_key="key")), received
    finally:
        if previous is None:
            del os.environ["VEVAL_INTERNAL_ENDPOINT"]
        else:
            os.environ["VEVAL_INTERNAL_ENDPOINT"] = previous
        server.shutdown()
        server.server_close()


def _raised(coro) -> VevalRequestError:
    try:
        asyncio.run(coro)
    except VevalRequestError as ex:
        return ex
    raise AssertionError("expected VevalRequestError")


def test_saving_from_a_trace_pins_the_baseline_by_default():
    with api() as (sdk, received):
        asyncio.run(sdk.save_snapshot_async("baseline", "tr_1"))

    assert received == [{"name": "baseline", "trace_id": "tr_1", "pinned": True}]


def test_saving_from_a_run_pins_the_baseline_by_default():
    async def plan():
        return "out"

    async def go(sdk):
        ctx = VevalExecutionContext("tr_run")
        await ctx.track_step_async("plan", "in", plan)
        await sdk.save_snapshot_async("baseline", ctx)

    with api() as (sdk, received):
        asyncio.run(go(sdk))

    assert len(received) == 1
    assert received[0]["source_trace_id"] == "tr_run"
    assert received[0]["pinned"] is True


def test_pinned_false_saves_an_unpinned_baseline():
    with api() as (sdk, received):
        asyncio.run(sdk.save_snapshot_async("baseline", "tr_1", SaveSnapshotOptions(pinned=False)))

    assert received[0]["pinned"] is False


def test_at_the_pin_limit_saving_raises_with_the_limit_and_how_to_save_unpinned():
    with api(409, PIN_LIMIT_BODY) as (sdk, _):
        error = _raised(sdk.save_snapshot_async("baseline", "tr_1"))

    assert error.status == 409
    assert "pinned snapshot limit reached (3 of 3 on the free plan)" in str(error)
    assert "pinned=False" in str(error)


def test_a_save_the_api_rejects_raises_with_its_status():
    with api(404, '{"error":"trace \'tr_1\' not found"}') as (sdk, _):
        error = _raised(sdk.save_snapshot_async("baseline", "tr_1"))

    assert error.status == 404
    assert "not found" in str(error)
    assert "pinned=False" not in str(error)


def test_a_network_failure_raises_without_a_status():
    previous = os.environ.get("VEVAL_INTERNAL_ENDPOINT")
    os.environ["VEVAL_INTERNAL_ENDPOINT"] = "http://127.0.0.1:1"
    try:
        error = _raised(VevalSdk(VevalOptions(api_key="key")).save_snapshot_async("baseline", "tr_1"))
    finally:
        if previous is None:
            del os.environ["VEVAL_INTERNAL_ENDPOINT"]
        else:
            os.environ["VEVAL_INTERNAL_ENDPOINT"] = previous

    assert error.status is None
