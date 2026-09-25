"""Core SDK tests. Run with `pytest sdk/python/tests` or `python sdk/python/tests/test_sdk.py`."""
import asyncio
import os
import sys
import warnings
from datetime import datetime, timezone

# Point every SDK at a closed local port: reporting calls fail fast and are swallowed, and nothing
# reaches the real API. Set before any SDK is constructed (the endpoint is read at construction).
os.environ["VEVAL_INTERNAL_ENDPOINT"] = "http://127.0.0.1:1"
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from veval import (  # noqa: E402
    ReplayOptions, ScenarioItem, StepData, TraceAssert, TraceData, VevalExecutionContext,
    VevalOptions, VevalSdk, VevalTestSdk,
)


def sdk():
    return VevalSdk(VevalOptions(api_key="test"))


def make_test_sdk():
    return VevalTestSdk(VevalOptions(api_key="test"))


def returning(value):
    # A zero-argument callback: track_step_async passes a StepHandle to callbacks that take parameters.
    async def produce():
        return value
    return produce


def run(*steps):
    async def go():
        ctx = VevalExecutionContext("tr_run")
        for name, inp, out in steps:
            await ctx.track_step_async(name, inp, returning(out))
        return ctx
    return asyncio.run(go())


def recording(*steps):
    return TraceData(trace_id="tr_recorded", input="hello", status="success", steps=list(steps))


def recorded(name, inp, out, type="llm"):
    return StepData(step_id=name, name=name, type=type, input=inp, output=out, status="success")


# --- options -----------------------------------------------------------------------------------------

def test_endpoint_comes_from_env_not_a_public_option():
    options = VevalOptions(api_key="k")
    assert options._endpoint == "http://127.0.0.1:1"
    assert not hasattr(options, "endpoint")


def test_project_id_is_deprecated_and_not_sent():
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        VevalOptions(api_key="k", project_id="p")
    assert any(issubclass(w.category, DeprecationWarning) for w in caught)

    now = datetime.now(timezone.utc)
    payload = sdk()._build_payload("tr", "agent", run(("s", "in", "out")), None, None, "success", None, now, now)
    assert "project_id" not in payload


# --- running and steps -------------------------------------------------------------------------------

def test_run_async_returns_result_and_records_steps_with_metadata():
    seen = {}

    async def agent(ctx):
        seen["ctx"] = ctx
        ctx.set_metadata("user_id", "u_1")

        async def call(step):
            step.set_meta("type", "llm")
            step.set_meta("cost_usd", 0.01)
            step.set_meta("provider", "anthropic")
            return "answer"
        return await ctx.track_step_async("call-llm", "prompt", call)

    assert asyncio.run(sdk().run_async("agent", agent)) == "answer"
    step = seen["ctx"].steps[0]
    assert (step.name, step.type, step.cost_usd, step.output, step.status) == ("call-llm", "llm", 0.01, "answer", "success")
    assert step.metadata["provider"] == "anthropic"
    assert seen["ctx"].trace_meta["user_id"] == "u_1"


def test_run_async_reraises_agent_errors_and_marks_step_failed():
    seen = {}

    async def boom():
        raise RuntimeError("kaboom")

    async def agent(ctx):
        seen["ctx"] = ctx
        return await ctx.track_step_async("boom", None, boom)

    try:
        asyncio.run(sdk().run_async("agent", agent))
        raise AssertionError("expected the agent error to propagate")
    except RuntimeError as ex:
        assert str(ex) == "kaboom"
    assert (seen["ctx"].steps[0].status, seen["ctx"].steps[0].error) == ("error", "kaboom")


# --- replay ------------------------------------------------------------------------------------------

def test_replay_serves_recorded_outputs_and_never_runs_the_step():
    ran = []

    async def live():
        ran.append(True)
        return "live"

    async def agent(ctx):
        return await ctx.track_step_async("classify", "hello", live)

    result = asyncio.run(sdk().replay_async(recording(recorded("classify", "hello", "greeting")), agent, ReplayOptions(mock_llm_responses=True)))
    assert result.output == "greeting"
    assert ran == []
    assert result.passed


def test_replay_fails_when_a_step_has_no_recording_instead_of_calling_live():
    async def agent(ctx):
        return await ctx.track_step_async("unrecorded", None, returning("live"))

    result = asyncio.run(sdk().replay_async(recording(recorded("classify", "hello", "greeting")), agent, ReplayOptions(mock_llm_responses=True)))
    assert not result.passed
    assert "no mock output for step 'unrecorded'" in result.failures[0]


def test_with_replay_makes_the_production_entry_point_replay_unchanged():
    replaying = make_test_sdk().with_replay(recording(recorded("classify", "hello", "greeting")))

    async def agent(ctx):
        return await ctx.track_step_async("classify", "hello", returning("live"))

    assert asyncio.run(replaying.run_async("agent", agent)) == "greeting"
    assert replaying.last_status == "success"


# --- assertions --------------------------------------------------------------------------------------

def test_built_in_assertions_pass_and_fail_as_documented():
    async def build():
        ctx = VevalExecutionContext("tr")

        async def fetch(step):
            step.set_meta("type", "tool")
            step.set_meta("cost_usd", 0.2)
            return "Montreal weather"
        await ctx.track_step_async("fetch", "q", fetch)
        await ctx.track_step_async("write", "d", returning("briefing"))
        return ctx
    ctx = asyncio.run(build())
    check = lambda a: asyncio.run(a.evaluate_async(ctx))  # noqa: E731

    assert check(TraceAssert.no_errors()) is None
    assert check(TraceAssert.max_steps(2)) is None
    assert check(TraceAssert.max_steps(1)).startswith("MaxSteps")
    assert check(TraceAssert.step_exists("write")) is None
    assert check(TraceAssert.step_exists("nope")).startswith("StepExists")
    assert check(TraceAssert.tool_called("fetch")) is None
    assert check(TraceAssert.tool_called("write")).startswith("ToolCalled")
    assert check(TraceAssert.max_cost(0.1)).startswith("MaxCost")
    assert check(TraceAssert.output_contains("Montreal")) is None
    assert check(TraceAssert.output_contains("Paris")).startswith("OutputContains")


# --- judge -------------------------------------------------------------------------------------------

def test_judge_mock_passes_records_verdict_and_reports_failures():
    judged = make_test_sdk().with_judge_mock("polite", True, 0.9, "Friendly.").with_judge_mock("concise", False, 0.4, "Rambles.")
    ctx = run(("reply", "hi", "Hello there!"))

    assert asyncio.run(TraceAssert.judge(judged, "polite").evaluate_async(ctx)) is None
    assert asyncio.run(TraceAssert.judge(judged, "concise").evaluate_async(ctx)) == "Judge: concise (score 0.40) — Rambles."
    assert [(j["criteria"], j["passed"]) for j in ctx.judgments] == [("polite", True), ("concise", False)]


def test_judge_without_a_mock_fails_instead_of_making_a_billed_call():
    failure = asyncio.run(TraceAssert.judge(make_test_sdk(), "unmocked").evaluate_async(run(("reply", "hi", "yo"))))
    assert failure.startswith("Judge: evaluation failed")
    assert "no judge mock for criteria 'unmocked'" in failure


# --- scenarios ---------------------------------------------------------------------------------------

def test_scenario_runs_each_item_with_shared_and_per_item_assertions():
    async def agent(ctx):
        async def reply():
            if ctx.input == "crash":
                raise RuntimeError("agent crashed")
            return f"echo: {ctx.input}"
        return await ctx.track_step_async("reply", ctx.input, reply)

    result = asyncio.run(make_test_sdk().run_scenario_async("echo", agent, [TraceAssert.step_exists("reply")], [
        ScenarioItem(name="ok", input="hi", assertions=[TraceAssert.output_contains("echo: hi")]),
        ScenarioItem(name="wrong output", input="bye", assertions=[TraceAssert.output_contains("echo: hi")]),
        ScenarioItem(name="crash", input="crash"),
    ]))

    assert [r.passed for r in result.results] == [True, False, False]
    assert result.pass_count == 1
    assert any("agent crashed" in f for f in result.results[2].failures)


if __name__ == "__main__":
    tests = [f for name, f in sorted(globals().items()) if name.startswith("test_")]
    for t in tests:
        t()
    print(f"python sdk tests: {len(tests)} passed")
