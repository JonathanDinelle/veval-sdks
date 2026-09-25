from __future__ import annotations
from typing import Any, Optional, TYPE_CHECKING
from ._step import Step
from ._snapshots import SnapshotOptions, compare_snapshots

if TYPE_CHECKING:
    from ._context import VevalExecutionContext


class ITraceAssertion:
    async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
        raise NotImplementedError


def _flatten_steps(steps: list[Step]) -> list[Step]:
    result: list[Step] = []
    for s in steps:
        result.append(s)
        result.extend(_flatten_steps(s.children))
    return result


class TraceAssert:
    @staticmethod
    def max_steps(max: int) -> ITraceAssertion:
        class _Assertion(ITraceAssertion):
            async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
                count = len(_flatten_steps(ctx.steps))
                return f"MaxSteps: expected at most {max} steps, got {count}" if count > max else None
        return _Assertion()

    @staticmethod
    def no_errors() -> ITraceAssertion:
        class _Assertion(ITraceAssertion):
            async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
                errors = [s.name for s in _flatten_steps(ctx.steps) if s.status == "error"]
                return f"NoErrors: found {len(errors)} step(s) with error status: {', '.join(errors)}" if errors else None
        return _Assertion()

    @staticmethod
    def step_exists(step_name: str) -> ITraceAssertion:
        class _Assertion(ITraceAssertion):
            async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
                found = any(s.name == step_name for s in _flatten_steps(ctx.steps))
                return None if found else f"StepExists: step '{step_name}' not found"
        return _Assertion()

    @staticmethod
    def max_cost(max_cost: float) -> ITraceAssertion:
        class _Assertion(ITraceAssertion):
            async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
                total = sum(s.cost_usd or 0 for s in _flatten_steps(ctx.steps))
                return f"MaxCost: expected at most {max_cost}, got {total}" if total > max_cost else None
        return _Assertion()

    @staticmethod
    def max_duration(max_ms: int) -> ITraceAssertion:
        class _Assertion(ITraceAssertion):
            async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
                total = sum(s.duration_ms or 0 for s in _flatten_steps(ctx.steps))
                return f"MaxDuration: expected at most {max_ms}ms, got {total}ms" if total > max_ms else None
        return _Assertion()

    @staticmethod
    def output_contains(expected: str) -> ITraceAssertion:
        class _Assertion(ITraceAssertion):
            async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
                found = any(
                    expected in str(s.output)
                    for s in _flatten_steps(ctx.steps)
                    if s.output is not None
                )
                return None if found else f"OutputContains: no step output contains '{expected}'"
        return _Assertion()

    @staticmethod
    def tool_called(tool_name: str) -> ITraceAssertion:
        class _Assertion(ITraceAssertion):
            async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
                found = any(
                    s.type == "tool" and s.name == tool_name
                    for s in _flatten_steps(ctx.steps)
                )
                return None if found else f"ToolCalled: no tool step named '{tool_name}' was found"
        return _Assertion()

    @staticmethod
    def judge(
        veval: Any,
        criteria: str,
        model: Optional[str] = None,
        threshold: Optional[float] = None,
        reference_output: Optional[Any] = None,
        samples: Optional[int] = None,
    ) -> ITraceAssertion:
        """Scores the trace's output against a rubric using an LLM judge, evaluated server-side."""
        class _Assertion(ITraceAssertion):
            async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
                try:
                    result = await veval.judge_async(
                        criteria, ctx, model=model, threshold=threshold,
                        reference_output=reference_output, samples=samples,
                    )
                except Exception as ex:
                    # Never let a network/API failure during judging silently pass a test.
                    return f"Judge: evaluation failed — {ex}"

                ctx.record_judgment(criteria, result["score"], result["passed"], result["reasoning"])

                if result["passed"]:
                    return None
                return f"Judge: {criteria} (score {result['score']:.2f}) — {result['reasoning']}"
        return _Assertion()

    @staticmethod
    def matches_snapshot(
        snapshot_or_sdk: Any,
        snapshot_name: Optional[str] = None,
        options: Optional[SnapshotOptions] = None,
    ) -> ITraceAssertion:
        """
        Fails when the run's step sequence or step inputs differ from the snapshot — e.g. a changed
        prompt, a dropped or repeated call. Pass a SnapshotData, or (sdk, name) to load a stored
        baseline; a missing baseline fails the assertion.
        """
        by_name = snapshot_name is not None

        class _Assertion(ITraceAssertion):
            async def evaluate_async(self, ctx: VevalExecutionContext) -> Optional[str]:
                name = snapshot_name if by_name else snapshot_or_sdk.name
                try:
                    snapshot = await snapshot_or_sdk.get_snapshot_async(snapshot_name) if by_name else snapshot_or_sdk
                except Exception as ex:
                    # A baseline we couldn't load must fail loudly, never pass vacuously.
                    return f"MatchesSnapshot: could not load snapshot '{name}' — {ex}"
                if snapshot is None:
                    return f"MatchesSnapshot: no snapshot named '{name}'. Save one with save_snapshot_async first."

                diff = compare_snapshots(snapshot, ctx, options)
                return "MatchesSnapshot: " + diff.summary(name or snapshot.name) if diff.has_changes else None
        return _Assertion()
