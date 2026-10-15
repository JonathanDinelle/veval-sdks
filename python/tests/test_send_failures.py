"""A trace or scenario run that can't be delivered is logged by default and raised in strict mode.
Mirrors sdk/node/test/send-failures.test.ts."""
import asyncio
import logging
import os
import sys
import threading
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, HTTPServer

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from veval import ScenarioItem, VevalOptions, VevalSdk, VevalSendError, VevalTestSdk  # noqa: E402


class _Unauthorized(BaseHTTPRequestHandler):
    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length", 0)))
        body = b'{"error":"invalid api key"}'
        self.send_response(401)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


@contextmanager
def endpoint(url):
    # The endpoint is read when the options are constructed.
    previous = os.environ.get("VEVAL_INTERNAL_ENDPOINT")
    os.environ["VEVAL_INTERNAL_ENDPOINT"] = url
    try:
        yield
    finally:
        if previous is None:
            del os.environ["VEVAL_INTERNAL_ENDPOINT"]
        else:
            os.environ["VEVAL_INTERNAL_ENDPOINT"] = previous


@contextmanager
def unauthorized_api():
    server = HTTPServer(("127.0.0.1", 0), _Unauthorized)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        with endpoint(f"http://127.0.0.1:{server.server_port}"):
            yield
    finally:
        server.shutdown()
        server.server_close()


@contextmanager
def veval_warnings():
    records: list[str] = []

    class Capture(logging.Handler):
        def emit(self, record):
            if record.levelno == logging.WARNING:
                records.append(record.getMessage())

    handler = Capture()
    logger = logging.getLogger("veval")
    logger.addHandler(handler)
    try:
        yield records
    finally:
        logger.removeHandler(handler)


async def answer(ctx):
    return "answer"


async def crash(ctx):
    raise RuntimeError("agent crashed")


def test_rejected_trace_is_logged_with_its_status_and_the_run_still_succeeds():
    with unauthorized_api(), veval_warnings() as logged:
        sdk = VevalSdk(VevalOptions(api_key="wrong"))
        assert asyncio.run(sdk.run_async("agent", answer)) == "answer"

    assert len(logged) == 1
    assert "401" in logged[0] and "invalid api key" in logged[0]


def test_strict_mode_raises_a_rejected_trace():
    with unauthorized_api(), veval_warnings():
        sdk = VevalSdk(VevalOptions(api_key="wrong", raise_on_send_failure=True))
        try:
            asyncio.run(sdk.run_async("agent", answer))
            raise AssertionError("expected VevalSendError")
        except VevalSendError as ex:
            assert ex.status == 401


def test_strict_mode_raises_a_network_failure():
    with endpoint("http://127.0.0.1:1"), veval_warnings() as logged:
        sdk = VevalSdk(VevalOptions(api_key="k", raise_on_send_failure=True))
        try:
            asyncio.run(sdk.run_async("agent", answer))
            raise AssertionError("expected VevalSendError")
        except VevalSendError as ex:
            assert ex.status is None
    assert len(logged) == 1


def test_strict_mode_keeps_the_agents_own_error():
    for make in (VevalSdk, VevalTestSdk):
        with unauthorized_api(), veval_warnings() as logged:
            sdk = make(VevalOptions(api_key="wrong", raise_on_send_failure=True))
            try:
                asyncio.run(sdk.run_async("agent", crash))
                raise AssertionError("expected RuntimeError")
            except RuntimeError as ex:
                assert str(ex) == "agent crashed"
        assert any("401" in m for m in logged)


def test_strict_mode_raises_a_rejected_scenario_run():
    with unauthorized_api(), veval_warnings() as logged:
        sdk = VevalTestSdk(VevalOptions(api_key="wrong", raise_on_send_failure=True))
        try:
            asyncio.run(sdk.run_scenario_async("echo", answer, [], [ScenarioItem(name="a", input="hi")]))
            raise AssertionError("expected VevalSendError")
        except VevalSendError as ex:
            assert ex.status == 401
    assert any("scenario run 'echo'" in m for m in logged)


if __name__ == "__main__":
    tests = [f for name, f in sorted(globals().items()) if name.startswith("test_")]
    for t in tests:
        t()
    print(f"python send failure tests: {len(tests)} passed")
