"""Snapshot comparison tests. Run with `pytest sdk/python/tests` or `python sdk/python/tests/test_snapshots.py`."""
import asyncio
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from veval import (  # noqa: E402
    ReplayOptions, SnapshotData, SnapshotOptions, StepData, TraceAssert, TraceData,
    VevalExecutionContext, VevalOptions, VevalSdk, VevalTestSdk, compare_snapshots,
)


def _returning(value):
    # A zero-argument callback: track_step_async passes a StepHandle to callbacks that take parameters.
    async def produce():
        return value
    return produce


def run(*steps):
    async def go():
        ctx = VevalExecutionContext("tr_run")
        for name, inp, out in steps:
            await ctx.track_step_async(name, inp, _returning(out))
        return ctx
    return asyncio.run(go())


def recorded(name, inp, out, type="llm"):
    return StepData(step_id=name, name=name, type=type, input=inp, output=out, status="success")


def test_identical_run_has_no_changes():
    base = SnapshotData.from_context(run(("classify", "q", "a"), ("respond", "a", "b")))
    assert not compare_snapshots(base, run(("classify", "q", "a"), ("respond", "a", "b"))).has_changes


def test_extra_call_of_same_step_is_added():
    base = SnapshotData.from_context(run(("fetch", "x", "n"), ("write", "n", "b")))
    diff = compare_snapshots(base, run(("fetch", "x", "n"), ("write", "n", "b"), ("write", "n", "b")))
    assert [(c.kind, c.step_name, c.occurrence) for c in diff.changes] == [("Added", "write", 2)]
    assert diff.added_steps == ["write"]


def test_changed_prompt_is_input_change_with_line_diff():
    base = SnapshotData.from_context(run(("write", "System: overdue first.\nData", "b")))
    diff = compare_snapshots(base, run(("write", "System: alphabetical.\nData", "b")))
    assert diff.changes[0].kind == "InputChanged"
    assert "- System: overdue first." in diff.changes[0].detail
    assert "+ System: alphabetical." in diff.changes[0].detail


def test_prompt_inside_object_diffs_line_by_line():
    base = SnapshotData.from_context(run(("write", {"model": "m", "system": "You are helpful.\n1. Overdue first.\n2. Today."}, "b")))
    diff = compare_snapshots(base, run(("write", {"model": "m", "system": "You are helpful.\n1. Alphabetical.\n2. Today."}, "b")))
    lines = diff.changes[0].detail.split("\n")
    assert any(line.startswith("-") and line.lstrip("- ") == "1. Overdue first." for line in lines)
    assert any(line.startswith("+") and line.lstrip("+ ") == "1. Alphabetical." for line in lines)
    assert "\\n" not in diff.changes[0].detail and len(lines) < 6


def test_api_json_equals_live_object_regardless_of_key_order():
    base = SnapshotData.from_trace(TraceData(trace_id="tr", steps=[recorded("search", json.loads('{"limit": 5, "query": "w"}'), "r")]))
    assert not compare_snapshots(base, run(("search", {"query": "w", "limit": 5}, "r"))).has_changes


def test_ignore_fields():
    base = SnapshotData.from_context(run(("call", {"prompt": "p", "timestamp": "1"}, "r")))
    current = run(("call", {"prompt": "p", "timestamp": "2"}, "r"))
    assert compare_snapshots(base, current).has_changes
    assert not compare_snapshots(base, current, SnapshotOptions(ignore_fields={"timestamp"})).has_changes


def test_outputs_only_compared_when_enabled():
    base = SnapshotData.from_context(run(("tool", "in", "sunny")))
    current = run(("tool", "in", "snow"))
    assert not compare_snapshots(base, current).has_changes
    assert [c.kind for c in compare_snapshots(base, current, SnapshotOptions(compare_outputs=True)).changes] == ["OutputChanged"]


def test_moved_step():
    base = SnapshotData.from_context(run(("a", 1, 1), ("b", 1, 1), ("c", 1, 1)))
    diff = compare_snapshots(base, run(("b", 1, 1), ("c", 1, 1), ("a", 1, 1)))
    assert [c.kind for c in diff.changes] == ["Moved"]
    assert len(diff.order_changes) == 1


def test_legacy_snapshot_compares_sequence_only():
    legacy = SnapshotData.from_dict({"step_names": ["write"], "step_order": ["write"], "step_count": 1})
    diff = compare_snapshots(legacy, run(("write", "x", "b"), ("write", "y", "b")))
    assert not diff.compared_content
    assert "legacy snapshot" in diff.summary()


def test_replay_compare_with_recording_catches_output_bound_to_wrong_call():
    sdk = VevalSdk(VevalOptions(api_key="test"))
    trace = TraceData(trace_id="tr_rec", steps=[recorded("call-claude", "Classify", "category: urgent"), recorded("call-claude", "Answer", "briefing")])

    async def agent(ctx):
        async def live():
            return "live"
        return await ctx.track_step_async("call-claude", "Answer", live)

    result = asyncio.run(sdk.replay_async(trace, agent, ReplayOptions(mock_llm_responses=True, compare_with_recording=SnapshotOptions())))
    assert result.output == "category: urgent"
    assert not result.passed
    kinds = {c.kind for c in result.recording_diff.changes}
    assert {"InputChanged", "Removed"} <= kinds


def test_replay_keeps_recorded_type_so_tool_called_passes():
    sdk = VevalSdk(VevalOptions(api_key="test"))
    trace = TraceData(trace_id="tr", steps=[recorded("fetch-weather", "d", "sunny", type="tool")])

    async def agent(ctx):
        async def live():
            return "live"
        return await ctx.track_step_async("fetch-weather", "d", live)

    result = asyncio.run(sdk.replay_async(trace, agent, ReplayOptions(
        mock_llm_responses=True, assertions=[TraceAssert.tool_called("fetch-weather")], compare_with_recording=SnapshotOptions())))
    assert result.failures == []


def test_matches_snapshot_assertion():
    base = SnapshotData.from_context(run(("write", "overdue first", "b")))
    failure = asyncio.run(TraceAssert.matches_snapshot(base).evaluate_async(run(("write", "alphabetical", "b"))))
    assert failure.startswith("MatchesSnapshot:") and "+ alphabetical" in failure

    sdk = VevalTestSdk(VevalOptions(api_key="test")).with_snapshot("briefing", base)
    assert asyncio.run(TraceAssert.matches_snapshot(sdk, "briefing").evaluate_async(run(("write", "overdue first", "b")))) is None

    class Missing:
        async def get_snapshot_async(self, name):
            return None

    class Broken:
        async def get_snapshot_async(self, name):
            raise ConnectionError("connection refused")

    missing = asyncio.run(TraceAssert.matches_snapshot(Missing(), "nope").evaluate_async(run(("w", 1, 1))))
    assert "no snapshot named 'nope'" in missing
    broken = asyncio.run(TraceAssert.matches_snapshot(Broken(), "briefing").evaluate_async(run(("w", 1, 1))))
    assert "could not load snapshot 'briefing'" in broken and "connection refused" in broken


if __name__ == "__main__":
    tests = [f for name, f in sorted(globals().items()) if name.startswith("test_")]
    for t in tests:
        t()
    print(f"python snapshot tests: {len(tests)} passed")
