"""The synchronous API (run, track_step, get_trace, replay, run_scenario) and ctx.is_replay.
Run with `pytest sdk/python/tests`. The is_replay tests mirror sdk/node/test/is-replay.test.ts."""
import asyncio
import os
import sys

import pytest

os.environ.setdefault("VEVAL_INTERNAL_ENDPOINT", "http://127.0.0.1:1")  # never reach the real API
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from veval import (  # noqa: E402
    ReplayOptions, ScenarioItem, StepData, TraceAssert, TraceData, VevalOptions, VevalSdk, VevalTestSdk,
)


def sdk():
    veval = VevalSdk(VevalOptions(api_key="test"))
    veval.sent = []
    veval._client.send_trace = lambda payload, raise_on_failure=None: veval.sent.append(payload) or True
    return veval


def recorded(name, inp, out, type):
    return StepData(step_id=name, name=name, type=type, input=inp, output=out, status="success")


def booking_trace():
    return TraceData(trace_id="tr_booking", agent_name="booking", input="Friday", status="success", steps=[
        recorded("find_slot", "Friday", "10:00", "tool"),
        recorded("write_reply", "10:00", "Booked for 10:00.", "llm"),
    ])


def no_event_loop():
    try:
        asyncio.get_running_loop()
    except RuntimeError:
        return True
    return False


def booking_agent(calls):
    """A synchronous agent: plain functions all the way down."""
    def agent(ctx):
        calls.append(("loop free", no_event_loop()))
        slot = ctx.track_step("find_slot", ctx.input, lambda: "11:30")
        return ctx.track_step("write_reply", slot, lambda handle: handle.set_meta("type", "llm") or f"Booked for {slot}.")
    return agent


# --- synchronous API ---------------------------------------------------------------------------------

def test_run_traces_a_synchronous_agent_without_an_event_loop():
    veval, calls = sdk(), []

    output = veval.run("booking", booking_agent(calls), "Friday")

    assert output == "Booked for 11:30."
    assert calls == [("loop free", True)]
    [payload] = veval.sent
    assert payload["status"] == "success"
    assert [(s["name"], s["type"], s["output"]) for s in payload["steps"]] == [
        ("find_slot", "custom", "11:30"), ("write_reply", "llm", "Booked for 11:30."),
    ]


def test_run_sends_the_trace_and_reraises_when_the_agent_fails():
    veval = sdk()

    def failing(ctx):
        ctx.track_step("find_slot", "Friday", lambda: (_ for _ in ()).throw(ValueError("calendar down")))

    with pytest.raises(ValueError, match="calendar down"):
        veval.run("booking", failing, "Friday")
    [payload] = veval.sent
    assert payload["status"] == "error"
    assert payload["steps"][0]["status"] == "error"


def test_replay_serves_recorded_outputs_to_a_synchronous_agent():
    calls = []

    result = sdk().replay(booking_trace(), booking_agent(calls), ReplayOptions(
        mock_llm_responses=True, assertions=[TraceAssert.output_contains("10:00")],
    ))

    assert result.passed, result.failures
    assert result.output == "Booked for 10:00."
    assert calls == [("loop free", True)]
    assert result.fidelity.matched == 2


def test_replay_reports_assertion_failures_from_a_synchronous_call():
    result = sdk().replay(booking_trace(), booking_agent([]), ReplayOptions(
        mock_llm_responses=True, assertions=[TraceAssert.output_contains("12:00")],
    ))

    assert not result.passed
    assert any("12:00" in f for f in result.failures)


def test_sync_replay_with_assertions_inside_an_event_loop_points_to_replay_async():
    async def inside_a_loop():
        sdk().replay(booking_trace(), booking_agent([]), ReplayOptions(
            mock_llm_responses=True, assertions=[TraceAssert.no_errors()],
        ))

    with pytest.raises(RuntimeError, match="replay_async"):
        asyncio.run(inside_a_loop())


def test_get_trace_returns_none_for_a_trace_the_test_sdk_does_not_hold():
    veval = VevalTestSdk(VevalOptions(api_key="test")).with_replay(booking_trace())

    assert veval.get_trace("tr_booking").trace_id == "tr_booking"
    assert veval.get_trace("tr_other") is None


def test_test_sdk_run_replays_a_synchronous_agent_through_its_production_entry_point():
    veval = VevalTestSdk(VevalOptions(api_key="test")).with_replay(booking_trace())

    assert veval.run("booking", booking_agent([]), "Friday") == "Booked for 10:00."
    assert veval.last_status == "success"


def test_test_sdk_run_raises_when_a_recorded_step_is_never_called():
    veval = VevalTestSdk(VevalOptions(api_key="test")).with_replay(booking_trace())

    with pytest.raises(RuntimeError, match="never called"):
        veval.run("booking", lambda ctx: ctx.track_step("find_slot", "Friday", lambda: "unused"), "Friday")
    assert veval.last_status == "error"


def test_run_scenario_runs_a_synchronous_agent():
    veval = VevalTestSdk(VevalOptions(api_key="test")).with_replay(booking_trace())

    result = veval.run_scenario("booking", booking_agent([]), [TraceAssert.no_errors()], items=[
        ScenarioItem(name="recorded", trace_id="tr_booking"),
        ScenarioItem(name="synthetic", input="Monday"),
    ])

    assert result.passed, [r.failures for r in result.results]
    assert [r.context.steps[-1].output for r in result.results] == ["Booked for 10:00.", "Booked for 11:30."]


def test_async_replay_accepts_a_synchronous_agent():
    result = asyncio.run(sdk().replay_async(booking_trace(), booking_agent([]), ReplayOptions(mock_llm_responses=True)))

    assert result.passed, result.failures
    assert result.output == "Booked for 10:00."


# --- is_replay ---------------------------------------------------------------------------------------

def is_replay_of(seen):
    def agent(ctx):
        seen.append(ctx.is_replay)
        slot = ctx.track_step("find_slot", ctx.input, lambda: "11:30")
        return ctx.track_step("write_reply", slot, lambda: f"Booked for {slot}.")
    return agent


def test_is_replay_is_false_in_a_normal_run():
    seen = []
    sdk().run("booking", is_replay_of(seen), "Friday")
    assert seen == [False]


@pytest.mark.parametrize("options", [
    ReplayOptions(),
    ReplayOptions(mock_llm_responses=True),
    ReplayOptions(live_model_steps=True),
], ids=["unmocked", "mocked", "live"])
def test_is_replay_is_true_in_every_replay_mode(options):
    seen = []
    sdk().replay(booking_trace(), is_replay_of(seen), options)
    assert seen == [True]


def test_is_replay_is_true_under_the_test_sdk_with_replay():
    seen = []
    VevalTestSdk(VevalOptions(api_key="test")).with_replay(booking_trace()).run("booking", is_replay_of(seen), "Friday")
    assert seen == [True]


def test_is_replay_lets_a_live_replay_skip_a_side_effect_outside_any_step():
    sent_emails = []

    def agent(ctx):
        reply = ctx.track_step("write_reply", "10:00", lambda: "Booked for 10:00.")
        if not ctx.is_replay:
            sent_emails.append(reply)
        return reply

    trace = TraceData(trace_id="tr_reply", input="Friday", status="success",
                      steps=[recorded("write_reply", "10:00", "Booked for 10:00.", "llm")])
    sdk().run("booking", agent, "Friday")
    result = sdk().replay(trace, agent, ReplayOptions(live_model_steps=True))

    assert result.passed, result.failures
    assert sent_emails == ["Booked for 10:00."]


def test_is_replay_marks_trace_backed_scenario_items_but_not_synthetic_ones():
    seen = []
    veval = VevalTestSdk(VevalOptions(api_key="test")).with_replay(booking_trace())

    veval.run_scenario("booking", is_replay_of(seen), [], items=[
        ScenarioItem(name="recorded", trace_id="tr_booking"),
        ScenarioItem(name="synthetic", input="Monday"),
    ])

    assert seen == [True, False]
