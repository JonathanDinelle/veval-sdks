"""Live replay: model steps run for real, every other step is served from the recording.
Run with `pytest sdk/python/tests`."""
import asyncio
import os
import sys

import pytest

os.environ.setdefault("VEVAL_INTERNAL_ENDPOINT", "http://127.0.0.1:1")  # never reach the real API
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from veval._scenario_report import _run_payload  # noqa: E402
from veval import (  # noqa: E402
    ItemRunResult, ReplayStop, ScenarioRunResult, ReplayOptions, ReplayStepRule, ReplayStoppedError, ScenarioItem, ScenarioOptions, StepData, StepOptions, StepType,
    TraceAssert, TraceData, VevalExecutionContext, VevalOptions, VevalSdk, VevalTestSdk,
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


def run_scenario(prompt, item_mock=False, scenario_mock=False, on_portal_visit=None):
    """Runs a trace-backed scenario item through VevalSdk, with the trace served locally instead of from the API."""
    trace = briefing_trace()
    veval = sdk()

    async def get_trace(_trace_id):
        return trace
    veval.get_trace_async = get_trace
    return asyncio.run(veval.run_scenario_async(
        "briefing", briefing_agent(prompt, on_portal_visit), [TraceAssert.output_contains("OVERDUE")],
        [ScenarioItem(trace_id=trace.trace_id, mock_model_steps=item_mock)], ScenarioOptions(mock_model_steps=scenario_mock),
    ))


def test_trace_backed_scenario_catches_a_regressed_prompt_and_passes_the_unchanged_one():
    visits = []
    unchanged = run_scenario(PROMPT, on_portal_visit=lambda: visits.append(1))
    regressed = run_scenario(REGRESSED_PROMPT, on_portal_visit=lambda: visits.append(1))

    assert unchanged.passed, unchanged.results[0].failures
    assert not regressed.passed
    assert any("OutputContains" in f for f in regressed.results[0].failures)
    assert visits == [], "tool steps are served from the recording"


def test_trace_backed_scenario_mocked_by_the_item_or_the_scenario_cannot_see_the_prompt_change():
    assert run_scenario(REGRESSED_PROMPT, item_mock=True).passed  # mocked replay returns the old output
    assert run_scenario(REGRESSED_PROMPT, scenario_mock=True).passed


def test_test_sdk_scenario_always_replays_mocked():
    trace = briefing_trace()
    model_calls = []

    async def agent(ctx):
        await ctx.track_step_async("visit_portal", "today", returning("live"))
        return await ctx.track_step_async("write_briefing", REGRESSED_PROMPT, returning("live", lambda: model_calls.append(1)))

    test_sdk = VevalTestSdk(VevalOptions(api_key="test")).with_replay(trace)
    result = asyncio.run(test_sdk.run_scenario_async(
        "briefing", agent, [TraceAssert.output_contains("OVERDUE")], [ScenarioItem(trace_id=trace.trace_id)]))
    assert result.passed  # the test double serves the recorded model output
    assert model_calls == []  # and never makes a live model call


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


def test_live_replay_stops_inconclusive_on_an_unrecorded_tool_call():
    charged = []

    async def agent(ctx):
        await briefing_agent(PROMPT)(ctx)
        return await ctx.track_step_async("charge_card", 42, returning("ok", lambda: charged.append(1)))

    result = asyncio.run(sdk().replay_async(briefing_trace(), agent, ReplayOptions(
        live_model_steps=True, assertions=[TraceAssert.output_contains("never")],
    )))
    assert charged == []
    assert result.outcome == "inconclusive"
    assert not result.passed
    assert result.failures == []  # a stopped replay can't back up a verdict, so assertions are not evaluated
    stop = result.inconclusive
    assert (stop.step, stop.step_name, stop.reason) == (3, "charge_card", "no_recording")
    assert "the trace never recorded it" in stop.message
    assert "replay mocked to test this change" in stop.message


def test_live_replay_reports_unused_recordings_without_failing():
    async def agent(ctx):
        return await ctx.track_step_async("write_briefing", PROMPT, returning("OVERDUE: Dentist."))

    result = asyncio.run(sdk().replay_async(briefing_trace(), agent, ReplayOptions(live_model_steps=True)))
    assert result.passed  # a live model may legitimately take a different path
    assert [u.step_name for u in result.fidelity.unused] == ["visit_portal"]


def browser_trace():
    """A browser trace: the model fills a field, reads the page, and narrates what it does. Narration's text follows
    the model's wording and doesn't change the page."""
    return TraceData(trace_id="tr_browser", agent_name="browser", input="book", status="success", steps=[
        recorded("plan", "book", "fill date 2026-11-02", "llm"),
        recorded("narrate", {"text": "Filling the date"}, "ok", "custom"),
        recorded("fill", {"field": "date", "value": "2026-11-02"}, "filled", "tool"),
        recorded("page_snapshot", {"reasoning": "check the form", "tab": 1}, "<form date=2026-11-02>", "tool"),
        recorded("answer", "<form date=2026-11-02>", "Booked for Nov 2.", "llm"),
    ])


def browser_agent(date="2026-11-02", narration="Filling the date", reasoning="check the form", tools_run=None):
    """The browser agent; each argument is what the live model would now choose at that point."""
    def ran(name):
        return (lambda: tools_run.append(name)) if tools_run is not None else None

    async def agent(ctx):
        await ctx.track_step_async("plan", "book", returning(f"fill date {date}"))
        await ctx.track_step_async("narrate", {"text": narration}, returning("ok", ran("narrate")))
        await ctx.track_step_async("fill", {"field": "date", "value": date}, returning("filled", ran("fill")))
        page = await ctx.track_step_async(
            "page_snapshot", {"reasoning": reasoning, "tab": 1}, returning("live page", ran("page_snapshot")))
        return await ctx.track_step_async("answer", page, returning("Booked for Nov 2."))
    return agent


def live_replay(agent, **options):
    return asyncio.run(sdk().replay_async(browser_trace(), agent, ReplayOptions(
        live_model_steps=True, assertions=[TraceAssert.output_contains("Booked")], **options,
    )))


def test_live_replay_stops_where_a_diverged_tool_step_leaves_the_recording():
    tools_run = []
    result = live_replay(browser_agent(date="2026-11-03", tools_run=tools_run))

    assert result.outcome == "inconclusive"
    assert result.failures == []
    stop = result.inconclusive
    assert (stop.step, stop.step_name, stop.reason) == (3, "fill", "diverged")
    assert '"value":"2026-11-02"' in stop.message
    assert '"value":"2026-11-03"' in stop.message
    assert "re-record, or replay mocked to test this change" in stop.message
    assert tools_run == []  # no tool runs for real in a live replay, and nothing runs after the stop
    steps = result.replayed_context.steps
    assert [s.name for s in steps] == ["plan", "narrate", "fill"]
    assert steps[-1].status == "error"
    assert result.status == "inconclusive"  # neither passed nor failed, so it mustn't raise the error alert
    assert result.summary == "inconclusive at step 3 'fill' — 2/5 recorded steps used, 0 diverged, 1 live, left the path"


def test_live_replay_passes_when_only_a_name_matched_narration_step_changes():
    result = live_replay(
        browser_agent(narration="Now I will fill in the date field"),
        stop_on_divergence_types={"tool", "custom"},
        step_rules={"narrate": ReplayStepRule(match_on_name_only=True)},
    )

    assert result.outcome == "passed", result.failures
    assert result.fidelity.diverged == 0


def test_live_replay_ignored_input_fields_dont_count_as_divergence():
    without_rule = live_replay(browser_agent(reasoning="make sure the date stuck"))
    with_rule = live_replay(
        browser_agent(reasoning="make sure the date stuck"),
        step_rules={"page_snapshot": ReplayStepRule(ignore_input_fields={"reasoning"})},
    )

    assert without_rule.inconclusive.step_name == "page_snapshot"
    assert with_rule.outcome == "passed", with_rule.failures


def test_live_replay_ignores_fields_inside_a_json_string_input():
    # A model's tool-call arguments often arrive as a JSON string rather than an object.
    trace = browser_trace()
    trace.steps[3].input = '{"reasoning":"check the form","tab":1}'

    async def agent(ctx):
        await ctx.track_step_async("plan", "book", returning("fill"))
        await ctx.track_step_async("narrate", {"text": "Filling the date"}, returning("ok"))
        await ctx.track_step_async("fill", {"field": "date", "value": "2026-11-02"}, returning("filled"))
        page = await ctx.track_step_async(
            "page_snapshot", '{"tab": 1, "reasoning": "make sure the date stuck"}', returning("live"))
        return await ctx.track_step_async("answer", page, returning("Booked for Nov 2."))

    result = asyncio.run(sdk().replay_async(trace, agent, ReplayOptions(
        live_model_steps=True, step_rules={"page_snapshot": ReplayStepRule(ignore_input_fields={"reasoning"})},
    )))
    assert result.outcome == "passed", result.inconclusive and result.inconclusive.message


def test_replay_step_rule_rejects_values_it_would_misread():
    with pytest.raises(ValueError):
        ReplayStepRule(on_divergence="Stop")
    with pytest.raises(TypeError):
        ReplayStepRule(ignore_input_fields="reasoning")


def test_live_replay_serves_a_diverged_untyped_step_by_default_and_reports_it():
    result = live_replay(browser_agent(narration="Typing the date"))

    assert result.outcome == "passed", result.failures
    assert [b.step_name for b in result.fidelity.bindings if b.kind == "diverged"] == ["narrate"]


def test_live_replay_a_step_rule_overrides_the_type_default():
    served = live_replay(browser_agent(date="2026-11-03"), step_rules={"fill": ReplayStepRule(on_divergence="serve")})
    stopped = live_replay(
        browser_agent(narration="Typing the date"), step_rules={"narrate": ReplayStepRule(on_divergence="stop")})

    assert served.outcome == "passed", served.failures
    assert served.fidelity.diverged == 1
    assert stopped.inconclusive.step_name == "narrate"


def test_live_replay_stays_inconclusive_when_the_agent_catches_the_stop():
    later_step_raised = []

    async def agent(ctx):
        try:
            await ctx.track_step_async("fill", {"field": "date", "value": "tomorrow"}, returning("filled"))
        except Exception:
            pass  # an agent that retries or swallows tool errors
        try:
            await ctx.track_step_async("page_snapshot", {"reasoning": "check the form", "tab": 1}, returning("live"))
        except ReplayStoppedError:
            later_step_raised.append(True)
        return "Booked anyway"

    result = live_replay(agent)

    assert later_step_raised == [True]  # every step after the stop raises it again
    assert result.outcome == "inconclusive"
    assert result.inconclusive.step_name == "fill"
    assert result.failures == []


def test_live_replay_stops_a_synchronous_step_too():
    def agent(ctx):
        ctx.track_step("plan", "book", lambda: "fill date 2026-11-03")
        ctx.track_step("narrate", {"text": "Filling the date"}, lambda: "ok")
        ctx.track_step("fill", {"field": "date", "value": "2026-11-03"}, lambda: "filled")
        return "Booked"

    result = sdk().replay(browser_trace(), agent, ReplayOptions(live_model_steps=True))

    assert result.outcome == "inconclusive"
    assert result.inconclusive.step_name == "fill"


def test_mocked_replay_still_serves_a_diverged_tool_step():
    result = asyncio.run(sdk().replay_async(
        browser_trace(), browser_agent(date="2026-11-03"), ReplayOptions(mock_llm_responses=True)))

    assert result.inconclusive is None  # stopping on divergence is for live replays; mocked replay serves every step
    assert [b.step_name for b in result.fidelity.bindings if b.kind == "diverged"] == ["fill"]


def test_scenario_counts_an_inconclusive_item_apart_and_an_item_rule_replaces_the_scenario_rule():
    trace = browser_trace()
    veval = sdk()

    async def get_trace(_trace_id):
        return trace
    veval.get_trace_async = get_trace

    result = asyncio.run(veval.run_scenario_async(
        "browser", browser_agent(date="2026-11-03"), [],
        [
            ScenarioItem(name="stops", trace_id=trace.trace_id),
            ScenarioItem(name="served", trace_id=trace.trace_id,
                         step_rules={"fill": ReplayStepRule(on_divergence="serve")}),
        ],
        ScenarioOptions(step_rules={"fill": ReplayStepRule(on_divergence="stop")}),
    ))

    assert not result.passed
    assert (result.pass_count, result.fail_count, result.inconclusive_count) == (1, 0, 1)
    assert result.results[0].inconclusive.step_name == "fill"
    assert not result.results[0].passed
    assert result.results[1].passed, result.results[1].failures


def passed_item():
    return ItemRunResult(item=ScenarioItem())


def inconclusive_item():
    return ItemRunResult(item=ScenarioItem(), inconclusive=ReplayStop(3, "fill", "diverged", None, None, "stopped"))


def test_scenario_run_fails_on_any_inconclusive_item_by_default():
    result = ScenarioRunResult(results=[passed_item(), passed_item(), passed_item(), inconclusive_item()])

    assert not result.passed  # a change that sends items off their recorded path mustn't look green
    assert result.inconclusive_rate == 0.25
    assert result.summary == "failed — 3 passed, 0 failed, 1 inconclusive (25% > 0% allowed)"


def test_scenario_run_passes_when_the_inconclusive_rate_is_within_the_threshold():
    result = ScenarioRunResult(
        max_inconclusive_rate=0.25, results=[passed_item(), passed_item(), passed_item(), inconclusive_item()])

    assert result.passed
    assert result.summary == "passed — 3 passed, 0 failed, 1 inconclusive (25%, within 25% allowed)"


def test_scenario_run_a_failed_item_fails_the_run_whatever_the_threshold():
    failed = ItemRunResult(item=ScenarioItem(), failures=["OutputContains failed"])

    assert not ScenarioRunResult(max_inconclusive_rate=1, results=[passed_item(), failed]).passed


@pytest.mark.parametrize("rate", [-0.1, 1.5, float("nan")])
def test_scenario_options_reject_a_threshold_outside_zero_to_one(rate):
    with pytest.raises(ValueError):
        ScenarioOptions(max_inconclusive_rate=rate)


def test_scenario_run_reports_how_many_replays_stayed_on_path():
    on_path = live_replay(browser_agent())
    off_path = live_replay(browser_agent(date="2026-11-03"))
    result = ScenarioRunResult(results=[
        ItemRunResult(item=ScenarioItem(), fidelity=on_path.fidelity),
        ItemRunResult(item=ScenarioItem(), fidelity=off_path.fidelity, inconclusive=off_path.inconclusive),
        passed_item(),
    ])

    assert result.stayed_on_path_rate == 0.5  # the synthetic item wasn't replayed, so it doesn't count
    assert result.summary.endswith(", 1/2 replays stayed on path")
    assert on_path.summary == "passed — 5/5 recorded steps used, 0 diverged, 2 live, stayed on path"


def test_scenario_run_payload_posts_a_third_outcome_and_the_stop():
    stop = ReplayStop(3, "fill", "diverged", {"value": "a"}, {"value": "b"}, "stopped")
    result = ScenarioRunResult(results=[
        ItemRunResult(item=ScenarioItem(name="ok")),
        ItemRunResult(item=ScenarioItem(name="bad"), failures=["OutputContains failed"]),
        ItemRunResult(item=ScenarioItem(name="off"), inconclusive=stop),
    ])

    payload = _run_payload(result, "synthetic")

    assert payload["fail_count"] == 1
    assert payload["inconclusive_count"] == 1
    assert payload["max_inconclusive_rate"] == 0
    assert payload["summary"].startswith("failed — 1 passed, 1 failed, 1 inconclusive")
    assert [i["outcome"] for i in payload["results"]] == ["passed", "failed", "inconclusive"]
    off = payload["results"][2]
    assert off["passed"] is False
    assert off["failures"] == []
    assert off["summary"] == "inconclusive at step 3 'fill'"
    assert off["inconclusive"] == {
        "step": 3, "step_name": "fill", "reason": "diverged",
        "recorded_input": {"value": "a"}, "actual_input": {"value": "b"}, "message": "stopped",
    }
    assert payload["results"][0]["inconclusive"] is None


# VEV-37: the call site can say a step is a model call before it runs.

def test_live_replay_runs_a_model_step_the_recording_never_contained_when_the_call_site_says_llm():
    extracted = []

    async def agent(ctx):
        briefing = await briefing_agent(PROMPT)(ctx)
        await ctx.track_step_async(
            "extraction", briefing, returning("Dentist", lambda: extracted.append(1)), StepOptions(type=StepType.LLM))
        return briefing

    result = asyncio.run(sdk().replay_async(briefing_trace(), agent, ReplayOptions(
        live_model_steps=True, assertions=[TraceAssert.output_contains("OVERDUE")],
    )))
    assert result.outcome == "passed", result.inconclusive and result.inconclusive.message
    assert extracted == [1]  # a model step runs live in a live replay
    last = result.fidelity.bindings[-1]
    assert (last.step_name, last.kind, last.recorded_index) == ("extraction", "live", None)
    assert result.replayed_context.steps[-1].type == "llm"


@pytest.mark.parametrize("step_type", [None, "tool"])
def test_live_replay_still_stops_on_an_unrecorded_step_not_marked_llm(step_type):
    ran = []

    async def agent(ctx):
        await briefing_agent(PROMPT)(ctx)
        return await ctx.track_step_async("charge_card", 42, returning("ok", lambda: ran.append(1)), StepOptions(type=step_type))

    result = asyncio.run(sdk().replay_async(briefing_trace(), agent, ReplayOptions(live_model_steps=True)))
    assert ran == []
    assert (result.inconclusive.step_name, result.inconclusive.reason) == ("charge_card", "no_recording")


def test_live_replay_the_recorded_type_wins_over_the_call_site_and_the_conflict_is_reported():
    visits = []

    async def agent(ctx):
        # A mislabelled tool call: the recording knows visit_portal is a tool, so it is still served, not run.
        await ctx.track_step_async(
            "visit_portal", "today", returning("live", lambda: visits.append(1)), StepOptions(type=StepType.LLM))
        return await ctx.track_step_async("write_briefing", PROMPT, returning(fake_model(PROMPT)))

    result = asyncio.run(sdk().replay_async(briefing_trace(), agent, ReplayOptions(live_model_steps=True)))
    assert visits == []
    assert result.outcome == "passed", result.failures
    portal, briefing = result.fidelity.bindings
    assert (portal.kind, portal.declared_type) == ("matched", "llm")
    assert briefing.declared_type is None  # no type was declared at that call site


def test_mocked_replay_still_refuses_an_unrecorded_model_step():
    called = []

    async def agent(ctx):
        await briefing_agent(PROMPT)(ctx)
        return await ctx.track_step_async(
            "extraction", "x", returning("y", lambda: called.append(1)), StepOptions(type=StepType.LLM))

    result = asyncio.run(sdk().replay_async(briefing_trace(), agent, ReplayOptions(mock_llm_responses=True)))
    assert called == []  # mocked replay never makes a model call
    assert any("no mock output for step 'extraction'" in f for f in result.failures), result.failures


def test_a_recorded_run_carries_the_call_site_type_and_set_meta_still_overrides_it():
    ctx = VevalExecutionContext("tr_x", None)

    def lookup(handle):
        handle.set_meta("type", "tool")
        return "y"

    ctx.track_step("extraction", "x", lambda: "y", StepOptions(type=StepType.LLM))
    ctx.track_step("lookup", "x", lookup, StepOptions(type=StepType.LLM))
    ctx.track_step("plain", "x", lambda: "y")
    assert [s.type for s in ctx.steps] == ["llm", "tool", "custom"]


def test_options_reject_the_old_positional_arguments():
    with pytest.raises(TypeError):
        StepOptions(type=1)
    with pytest.raises(TypeError):
        asyncio.run(sdk().run_scenario_async("s", lambda ctx: "x", [], [], True))


def test_live_replay_runs_a_step_recorded_untyped_when_the_call_site_says_llm():
    # An older trace, or a model call that threw before set_meta ran: the model step was saved as custom.
    trace = briefing_trace()
    trace.steps[1].type = "custom"
    model_calls = []

    async def agent(ctx):
        await ctx.track_step_async("visit_portal", "today", returning("live"))
        return await ctx.track_step_async(
            "write_briefing", REGRESSED_PROMPT, returning(fake_model(REGRESSED_PROMPT), lambda: model_calls.append(1)),
            StepOptions(type=StepType.LLM))

    result = asyncio.run(sdk().replay_async(trace, agent, ReplayOptions(
        live_model_steps=True, assertions=[TraceAssert.output_contains("OVERDUE")],
    )))
    assert model_calls == [1]  # custom says nothing about the step, so the call site's llm applies
    assert result.outcome == "failed"  # the regressed prompt ran live and lost the overdue-first rule
    last = result.fidelity.bindings[-1]
    assert (last.kind, last.declared_type) == ("live", None)

