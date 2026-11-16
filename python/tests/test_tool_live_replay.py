"""Tool-live replay: tools run for real against their recorded inputs, the model is served from the recording.
Run with `pytest sdk/python/tests`."""
import os
import sys

import pytest

os.environ.setdefault("VEVAL_INTERNAL_ENDPOINT", "http://127.0.0.1:1")  # never reach the real API
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from veval._sdk import _scenario_replay_options  # noqa: E402
from veval import (  # noqa: E402
    ReplayFault, ReplayOptions, ReplayStepRule, ScenarioItem, ScenarioOptions, StepData, StepOptions, StepType,
    TraceData, VevalOptions, VevalSdk, VevalTestSdk,
)


def recorded(name, inp, out, type):
    return StepData(step_id=name, name=name, type=type, input=inp, output=out, status="success")


def trace():
    """A recorded run: the model picks a product, a pure tool formats its price, the model writes the answer."""
    return TraceData(trace_id="tr_quote", agent_name="quote", input="how much is the lamp?", status="success", steps=[
        recorded("pick_product", "how much is the lamp?", "lamp", "llm"),
        recorded("format_price", "lamp", "EUR 12.50", "tool"),
        recorded("answer", "price: EUR 12.50", "The lamp costs EUR 12.50.", "llm"),
    ])


def sdk():
    return VevalSdk(VevalOptions(api_key="test"))


def tool_live(**kwargs):
    return ReplayOptions(live_tool_steps=True, **kwargs)


LLM = StepOptions(type=StepType.LLM)
TOOL = StepOptions(type=StepType.TOOL)


def agent(fmt, on_model=None):
    """The agent under test; fmt is the tool's implementation."""
    def model():
        if on_model:
            on_model()
        return "live model"

    def run(ctx):
        product = ctx.track_step("pick_product", ctx.input, model, LLM)
        price = ctx.track_step("format_price", product, lambda: fmt(product), TOOL)
        return ctx.track_step("answer", f"price: {price}", model, LLM)
    return run


def test_unchanged_tool_runs_for_real_model_from_recording_passes():
    runs, model_calls = [], []
    result = sdk().replay(trace(), agent(lambda p: runs.append(p) or "EUR 12.50", lambda: model_calls.append(1)),
                          tool_live())

    assert result.outcome == "passed", result.failures
    assert len(runs) == 1
    assert model_calls == []
    assert result.output == "The lamp costs EUR 12.50."
    assert [c.changed for c in result.fidelity.tool_checks] == [False]
    assert result.replayed_context.steps[1].metadata["_source"] == "live_tool"
    assert "1 tools replayed, 0 not replayed, 0 outputs changed" in result.summary


def test_changed_tool_fails_with_both_outputs_and_the_agent_gets_the_new_output():
    result = sdk().replay(trace(), agent(lambda p: "EUR 1250"), tool_live())

    assert result.outcome == "failed"
    assert ("Tool output changed: 'format_price' (recorded step 2) ran with its recorded input and returned "
            '"EUR 1250" instead of "EUR 12.50".') in result.failures
    assert result.fidelity.tool_output_changes[0].actual_output == "EUR 1250"
    assert result.replayed_context.steps[2].input == "price: EUR 1250"
    assert "1 output changed" in result.summary


def test_object_outputs_compare_ignoring_key_order_and_ignored_fields():
    t = trace()
    t.steps[1].output = {"amount": 12.5, "currency": "EUR", "request_id": "r1"}

    def run(ctx):
        product = ctx.track_step("pick_product", ctx.input, lambda: "x")
        ctx.track_step("format_price", product, lambda: {"request_id": "r2", "currency": "EUR", "amount": 12.5}, TOOL)
        return "done"
    result = sdk().replay(t, run, tool_live(
        step_rules={"format_price": ReplayStepRule(ignore_output_fields={"request_id"})}))

    assert result.fidelity.tool_output_changes == []


def test_tool_called_with_an_unrecorded_input_is_inconclusive():
    def run(ctx):
        ctx.track_step("pick_product", ctx.input, lambda: "x")
        return ctx.track_step("format_price", "LAMP", lambda: "EUR 12.50", TOOL)
    result = sdk().replay(trace(), run, tool_live())

    assert result.outcome == "inconclusive"
    assert result.inconclusive.reason == "diverged"
    assert result.inconclusive.message.startswith("Tool-live replay stopped at step 2 ('format_price')")
    assert "The model is served from the recording" in result.inconclusive.message


def test_tool_the_recording_never_called_is_inconclusive():
    def run(ctx):
        ctx.track_step("lookup_stock", "lamp", lambda: 3, TOOL)
        return ""
    result = sdk().replay(trace(), run, tool_live())

    assert result.outcome == "inconclusive"
    assert result.inconclusive.reason == "no_recording"


def test_tool_that_reads_external_state_is_served_and_reported_not_replayed_never_as_a_pass():
    ran = []
    result = sdk().replay(trace(), agent(lambda p: ran.append(p) or "live"), tool_live(
        step_rules={"format_price": ReplayStepRule(reads_external_state=True)}))

    assert ran == []
    assert result.fidelity.not_replayed == 1
    assert result.outcome == "failed"
    assert len(result.failures) == 1
    assert result.failures[0].startswith("Tool-live replay of trace tr_quote ran no tool")
    assert "except 1 that read external state" in result.failures[0]


def test_tool_that_now_raises_is_a_change():
    def fmt(p):
        raise ValueError("bad currency")
    result = sdk().replay(trace(), agent(fmt), tool_live())

    assert ("Tool output changed: 'format_price' (recorded step 2) ran with its recorded input and raised "
            '(bad currency) where the recording returned "EUR 12.50".') in result.failures


def test_tool_that_failed_in_the_recording_and_still_fails_is_not_a_change():
    t = trace()
    t.steps[1].status = "error"
    t.steps[1].error = "bad currency"
    t.steps[1].output = None

    def fmt():
        raise ValueError("bad currency")

    def run(ctx):
        product = ctx.track_step("pick_product", ctx.input, lambda: "x")
        try:
            ctx.track_step("format_price", product, fmt, TOOL)
        except ValueError:
            pass
        return "fallback"
    result = sdk().replay(t, run, tool_live())

    assert [c.changed for c in result.fidelity.tool_checks] == [False]
    assert not any(f.startswith("Tool output changed") for f in result.failures)


def test_untyped_steps_are_served_from_the_recording():
    t = trace()
    t.steps.insert(2, recorded("narrate", "EUR 12.50", "narrated", "custom"))
    narrated = []

    def run(ctx):
        product = ctx.track_step("pick_product", ctx.input, lambda: "x")
        price = ctx.track_step("format_price", product, lambda: "EUR 12.50", TOOL)
        ctx.track_step("narrate", price, lambda: narrated.append(1) or "live")
        return ctx.track_step("answer", f"price: {price}", lambda: "live")
    result = sdk().replay(t, run, tool_live())

    assert result.outcome == "passed", result.failures
    assert narrated == []


def test_live_tool_steps_with_live_model_steps_raises():
    with pytest.raises(ValueError, match="can't be combined"):
        sdk().replay(trace(), agent(lambda p: p), tool_live(live_model_steps=True))


def test_faults_work_in_a_tool_live_replay():
    calls = []

    def fmt():
        calls.append(1)
        return "EUR 12.50"

    def run(ctx):
        product = ctx.track_step("pick_product", ctx.input, lambda: "x")
        try:
            price = ctx.track_step("format_price", product, fmt, TOOL)
        except TimeoutError:
            price = ctx.track_step("format_price", product, fmt, TOOL)
        return ctx.track_step("answer", f"price: {price}", lambda: "live")
    result = sdk().replay(trace(), run, tool_live(faults=[ReplayFault.timeout("format_price").times(1)]))

    assert result.outcome == "passed", result.failures
    assert calls == [1]


def test_scenario_live_tool_steps_runs_tools_in_the_test_double():
    runs = []
    veval = VevalTestSdk(VevalOptions(api_key="test")).with_replay(trace())
    run = veval.run_scenario("quote", agent(lambda p: runs.append(p) or "EUR 1250"), [],
                             [ScenarioItem(trace_id="tr_quote")], ScenarioOptions(live_tool_steps=True))

    assert len(runs) == 1
    assert not run.passed
    assert any(f.startswith("Tool output changed: 'format_price'") for f in run.results[0].failures)


def test_scenario_tool_live_wins_over_mocked():
    options = _scenario_replay_options(
        ScenarioItem(mock_model_steps=True, live_tool_steps=True), ScenarioOptions(), [])

    assert options.live_tool_steps
    assert not options.mock_llm_responses
    assert not options.live_model_steps
