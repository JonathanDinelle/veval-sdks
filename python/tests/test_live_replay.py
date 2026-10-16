"""Live replay: model steps run for real, every other step is served from the recording.
Run with `pytest sdk/python/tests`."""
import asyncio
import os
import sys

os.environ.setdefault("VEVAL_INTERNAL_ENDPOINT", "http://127.0.0.1:1")  # never reach the real API
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from veval import (  # noqa: E402
    ReplayOptions, ScenarioItem, StepData, TraceAssert, TraceData, VevalOptions, VevalSdk, VevalTestSdk,
)

PROMPT = "List overdue items first."
REGRESSED_PROMPT = "List items alphabetically."


def recorded(name, inp, out, type):
    return StepData(step_id=name, name=name, type=type, input=inp, output=out, status="success")


def briefing_trace():
    """A production trace: a portal visit (tool) and a model call that wrote the briefing."""
    return TraceData(trace_id="tr_briefing", agent_name="briefing", input="today", status="success", steps=[
        recorded("visit_portal", "today", "Dentist (overdue); Taxes", "tool"),
        recorded("write_briefing", PROMPT, "OVERDUE: Dentist. Then: Taxes.", "llm"),
    ])


def fake_model(prompt):
    """Stands in for the model: it only puts overdue items first when the prompt asks for it."""
    return "OVERDUE: Dentist. Then: Taxes." if "overdue" in prompt else "Dentist. Taxes."


def returning(value, on_call=None):
    async def produce():
        if on_call:
            on_call()
        return value
    return produce


def briefing_agent(prompt, on_portal_visit=None):
    async def agent(ctx):
        await ctx.track_step_async("visit_portal", "today", returning("live portal data", on_portal_visit))
        return await ctx.track_step_async("write_briefing", prompt, returning(fake_model(prompt)))
    return agent


def sdk():
    return VevalSdk(VevalOptions(api_key="test"))


def run_scenario(prompt, mock_model_steps=False, on_portal_visit=None):
    trace = briefing_trace()
    test_sdk = VevalTestSdk(VevalOptions(api_key="test")).with_replay(trace)
    return asyncio.run(test_sdk.run_scenario_async(
        "briefing", briefing_agent(prompt, on_portal_visit), [TraceAssert.output_contains("OVERDUE")],
        [ScenarioItem(trace_id=trace.trace_id, mock_model_steps=mock_model_steps)],
    ))


def test_trace_backed_scenario_catches_a_regressed_prompt_and_passes_the_unchanged_one():
    visits = []
    unchanged = run_scenario(PROMPT, on_portal_visit=lambda: visits.append(1))
    regressed = run_scenario(REGRESSED_PROMPT, on_portal_visit=lambda: visits.append(1))

    assert unchanged.passed, unchanged.results[0].failures
    assert not regressed.passed
    assert any("OutputContains" in f for f in regressed.results[0].failures)
    assert visits == [], "tool steps are served from the recording"


def test_trace_backed_scenario_with_mock_model_steps_cannot_see_the_prompt_change():
    assert run_scenario(REGRESSED_PROMPT, mock_model_steps=True).passed  # mocked replay returns the old output


def test_live_replay_reports_model_steps_as_live():
    result = asyncio.run(sdk().replay_async(briefing_trace(), briefing_agent(PROMPT), ReplayOptions(live_model_steps=True)))
    assert result.passed
    assert result.fidelity.live == 1
    assert [(b.step_name, b.kind) for b in result.fidelity.bindings] == [("visit_portal", "matched"), ("write_briefing", "live")]


def test_live_replay_serves_untyped_steps_from_the_recording():
    trace = briefing_trace()
    trace.steps.insert(0, recorded("load_profile", "u_1", "profile", "custom"))
    ran = []

    async def agent(ctx):
        profile = await ctx.track_step_async("load_profile", "u_1", returning("live", lambda: ran.append(1)))
        assert profile == "profile"
        return await briefing_agent(PROMPT)(ctx)

    result = asyncio.run(sdk().replay_async(trace, agent, ReplayOptions(live_model_steps=True)))
    assert result.passed
    assert ran == []


def test_live_replay_fails_when_no_step_ran_live():
    trace = briefing_trace()
    trace.steps[1].type = "custom"  # the model step was never marked as llm

    result = asyncio.run(sdk().replay_async(trace, briefing_agent(REGRESSED_PROMPT), ReplayOptions(live_model_steps=True)))
    assert not result.passed
    assert len(result.failures) == 1
    assert "ran no step live" in result.failures[0]
    assert 'set_meta("type", "llm")' in result.failures[0]


def test_live_replay_runs_an_extra_call_of_a_model_step_live():
    async def agent(ctx):
        await briefing_agent(PROMPT)(ctx)
        return await ctx.track_step_async("write_briefing", "Shorten it.", returning("OVERDUE: Dentist."))

    result = asyncio.run(sdk().replay_async(briefing_trace(), agent, ReplayOptions(live_model_steps=True)))
    assert result.passed
    assert result.output == "OVERDUE: Dentist."
    last = result.fidelity.bindings[-1]
    assert (last.kind, last.recorded_index) == ("live", None)


def test_live_replay_refuses_to_run_an_unrecorded_tool_call():
    charged = []

    async def agent(ctx):
        await briefing_agent(PROMPT)(ctx)
        return await ctx.track_step_async("charge_card", 42, returning("ok", lambda: charged.append(1)))

    result = asyncio.run(sdk().replay_async(briefing_trace(), agent, ReplayOptions(live_model_steps=True)))
    assert not result.passed
    assert len(result.failures) == 1
    assert "Live replay: step 'charge_card' has no recording" in result.failures[0]
    assert charged == []


def test_live_replay_reports_unused_recordings_without_failing():
    async def agent(ctx):
        return await ctx.track_step_async("write_briefing", PROMPT, returning("OVERDUE: Dentist."))

    result = asyncio.run(sdk().replay_async(briefing_trace(), agent, ReplayOptions(live_model_steps=True)))
    assert result.passed  # a live model may legitimately take a different path
    assert [u.step_name for u in result.fidelity.unused] == ["visit_portal"]
