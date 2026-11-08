"""Fault injection: replay serves a declared failure instead of a step's recorded output.
Run with `pytest sdk/python/tests`."""
import asyncio
import json
import os
import sys

import pytest

os.environ.setdefault("VEVAL_INTERNAL_ENDPOINT", "http://127.0.0.1:1")  # never reach the real API
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from veval import (  # noqa: E402
    FaultHttpError, MalformedValue, ReplayFault, ReplayOptions, StepData, StepOptions, StepType, TraceAssert,
    TraceData, VevalOptions, VevalSdk,
)

FLIGHTS = '["AF123","BA456"]'


def recorded(name, inp, out, type):
    return StepData(step_id=name, name=name, type=type, input=inp, output=out, status="success")


def trace():
    """A recorded run: one flight search, then a model call that summarizes it."""
    return TraceData(trace_id="tr_faults", agent_name="travel", input="Paris", status="success", steps=[
        recorded("search_flights", "Paris", FLIGHTS, "tool"),
        recorded("summarize", f"flights: {FLIGHTS}", "Found AF123", "llm"),
    ])


def sdk():
    return VevalSdk(VevalOptions(api_key="test"))


def agent(retries):
    """The agent under test: retries the search up to `retries` times, then summarizes."""
    def run(ctx):
        attempt = 0
        while True:
            try:
                flights = ctx.track_step("search_flights", "Paris", lambda: "live")
                break
            except Exception:
                if attempt >= retries:
                    raise
                attempt += 1
        return ctx.track_step("summarize", f"flights: {flights}", lambda: "live")
    return run


def mocked(*faults, assertions=None):
    return ReplayOptions(mock_llm_responses=True, faults=list(faults), assertions=assertions or [])


def caught_from(fault):
    """What the first step call raised under this fault."""
    caught = []

    def run(ctx):
        try:
            ctx.track_step("search_flights", "Paris", lambda: "live")
        except Exception as ex:
            caught.append(ex)
        return ""
    sdk().replay(trace(), run, mocked(fault))
    return caught[0]


def test_timeout_on_first_call_retry_recovers_passes():
    result = sdk().replay(trace(), agent(2), mocked(
        ReplayFault.timeout("search_flights").times(1),
        assertions=[TraceAssert.max_attempts("search_flights", 1), TraceAssert.no_errors()]))

    assert result.outcome == "passed", result.failures
    assert result.output == "Found AF123"
    assert [r.fired for r in result.faults] == [1]
    assert result.fidelity.faulted == 1
    assert result.fidelity.unused == []
    faulted = result.replayed_context.steps[0]
    assert faulted.status == "error"
    assert faulted.metadata["_source"] == "fault"
    assert faulted.type == "tool"
    assert "1 fault injected" in result.summary


def test_timeout_without_retry_policy_fails():
    result = sdk().replay(trace(), agent(0), mocked(ReplayFault.timeout("search_flights").times(1)))

    assert result.outcome == "failed"
    assert any("Replay threw exception" in f and "Injected fault" in f for f in result.failures)


def test_timeout_raises_timeout_error():
    assert isinstance(caught_from(ReplayFault.timeout("search_flights")), TimeoutError)


def test_limited_fault_swallowed_without_retry_fails_on_unused_recording():
    def run(ctx):
        try:
            return ctx.track_step("search_flights", "Paris", lambda: "live")
        except Exception:
            return "Sorry, try later"
    result = sdk().replay(trace(), run, mocked(ReplayFault.timeout("search_flights").times(1)))

    assert result.outcome == "failed"
    assert any("never called" in f for f in result.failures)


def test_every_call_fault_with_fallback_that_ends_the_run_passes():
    def run(ctx):
        try:
            return ctx.track_step("search_flights", "Paris", lambda: "live")
        except FaultHttpError as ex:
            if ex.status_code == 503:
                return "Search is down, try later"
            raise
    result = sdk().replay(trace(), run, mocked(
        ReplayFault.http_error("search_flights", 503), assertions=[TraceAssert.no_errors()]))

    assert result.outcome == "passed", result.failures
    assert result.output == "Search is down, try later"
    assert len(result.fidelity.unused) == 2


def test_fallback_tool_not_in_recording_is_inconclusive_in_mocked_mode():
    def run(ctx):
        try:
            return ctx.track_step("search_flights", "Paris", lambda: "live")
        except FaultHttpError:
            return ctx.track_step("search_trains", "Paris", lambda: "live", StepOptions(type=StepType.TOOL))
    result = sdk().replay(trace(), run, mocked(ReplayFault.http_error("search_flights", 500)))

    assert result.outcome == "inconclusive"
    assert not result.passed
    assert result.inconclusive.step_name == "search_trains"
    assert result.inconclusive.reason == "no_recording"
    assert result.inconclusive.message.startswith(
        "Replay stopped at step 2 ('search_trains') after an injected fault (HTTP 500 on 'search_flights')")
    assert result.status == "inconclusive"


def test_after_fault_model_call_carrying_the_error_is_inconclusive_in_mocked_mode():
    def run(ctx):
        try:
            flights = ctx.track_step("search_flights", "Paris", lambda: "live")
        except Exception as ex:
            flights = f"error: {ex}"
        return ctx.track_step("summarize", f"flights: {flights}", lambda: "live")
    result = sdk().replay(trace(), run, mocked(ReplayFault.timeout("search_flights")))

    assert result.outcome == "inconclusive"
    assert result.inconclusive.step_name == "summarize"
    assert result.inconclusive.reason == "diverged"


def test_live_replay_model_step_after_fault_runs_live():
    model_saw = []

    async def run(ctx):
        try:
            flights = await ctx.track_step_async("search_flights", "Paris", lambda: _value("live"))
        except Exception:
            flights = "unavailable"

        async def model():
            model_saw.append(flights)
            return "No flights right now"
        return await ctx.track_step_async("summarize", f"flights: {flights}", model)

    result = asyncio.run(sdk().replay_async(trace(), run, ReplayOptions(
        live_model_steps=True, faults=[ReplayFault.timeout("search_flights")])))

    assert result.outcome == "passed", result.failures
    assert model_saw == ["unavailable"]
    assert result.output == "No flights right now"


async def _value(v):
    return v


def test_rate_limit_carries_status_and_retry_after():
    caught = caught_from(ReplayFault.rate_limit("search_flights", 2))
    assert isinstance(caught, FaultHttpError)
    assert caught.status_code == 429
    assert caught.retry_after_seconds == 2
    assert caught.headers["Retry-After"] == "2"


def test_custom_exception_is_raised_as_built():
    class VendorError(Exception):
        pass
    caught = caught_from(ReplayFault.throw("search_flights", lambda: VendorError("vendor down")))
    assert isinstance(caught, VendorError)
    assert str(caught) == "vendor down"


def test_truncated_output_is_half_the_recorded_json_then_guard_retries():
    outputs = []

    def run(ctx):
        while True:
            raw = ctx.track_step("search_flights", "Paris", lambda: "live")
            outputs.append(raw)
            try:
                json.loads(raw)
            except ValueError:
                continue
            return ctx.track_step("summarize", f"flights: {raw}", lambda: "live")
    result = sdk().replay(trace(), run, mocked(
        ReplayFault.malformed("search_flights", "truncated").times(1),
        assertions=[TraceAssert.max_attempts("search_flights", 1), TraceAssert.no_errors()]))

    assert result.outcome == "passed", result.failures
    assert outputs == ['["AF123"', FLIGHTS]


def test_empty_and_value_malformed_outputs():
    seen = []

    def run(ctx):
        seen.append(ctx.track_step("search_flights", "Paris", lambda: "live"))
        seen.append(ctx.track_step("search_flights", "Paris", lambda: "live"))
        return ""
    sdk().replay(trace(), run, mocked(
        ReplayFault.malformed("search_flights", "empty").times(1),
        ReplayFault.malformed("search_flights", MalformedValue(42)).times(1)))

    assert seen == ["", 42]


def test_fault_that_never_fires_fails_the_replay():
    result = sdk().replay(trace(), agent(0), mocked(ReplayFault.timeout("book_hotel")))

    assert result.outcome == "failed"
    assert len(result.failures) == 1
    assert result.failures[0].startswith(
        "Fault not triggered: Timeout on 'book_hotel' never fired: the agent never called 'book_hotel'")
    assert not result.faults[0].triggered


def test_when_input_faults_only_matching_calls():
    result = sdk().replay(trace(), agent(0), mocked(ReplayFault.timeout("search_flights").when_input("Rome")))

    assert len(result.failures) == 1
    assert "none of the 1 call(s) of 'search_flights' matched it" in result.failures[0]


def test_on_call_skips_earlier_calls():
    t = trace()
    t.steps.insert(1, recorded("search_flights", "Paris", FLIGHTS, "tool"))

    def run(ctx):
        ctx.track_step("search_flights", "Paris", lambda: "live")
        try:
            ctx.track_step("search_flights", "Paris", lambda: "live")
        except TimeoutError:
            ctx.track_step("search_flights", "Paris", lambda: "live")
        return ctx.track_step("summarize", f"flights: {FLIGHTS}", lambda: "live")
    result = sdk().replay(t, run, mocked(ReplayFault.timeout("search_flights").on_call(2).times(1)))

    assert result.outcome == "passed", result.failures
    assert [s.status for s in result.replayed_context.steps] == ["success", "error", "success", "success"]


def test_max_attempts_too_many_retries_fails():
    result = sdk().replay(trace(), agent(5), mocked(
        ReplayFault.timeout("search_flights").times(3), assertions=[TraceAssert.max_attempts("search_flights", 2)]))

    assert result.failures == [
        "MaxAttempts: expected at most 2 call(s) of 'search_flights' after its first failure, got 3"]


def test_max_attempts_step_that_never_failed_fails():
    result = sdk().replay(trace(), agent(0), ReplayOptions(
        mock_llm_responses=True, assertions=[TraceAssert.max_attempts("search_flights", 2)]))

    assert len(result.failures) == 1
    assert "never failed" in result.failures[0]


def test_faults_without_mocked_or_live_replay_raise():
    with pytest.raises(ValueError, match="mocked or live replay"):
        sdk().replay(trace(), agent(0), ReplayOptions(faults=[ReplayFault.timeout("search_flights")]))


def test_description_names_kind_step_and_scope():
    assert ReplayFault.http_error("book", 503).times(2).description == "HTTP 503 on 'book' (first 2 calls)"
    assert ReplayFault.rate_limit("llm").description == "Rate limit (429, Retry-After 1s) on 'llm'"
    with pytest.raises(ValueError):
        ReplayFault.http_error("book", 200)
