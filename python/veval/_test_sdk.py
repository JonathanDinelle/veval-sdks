from __future__ import annotations
import uuid
from collections import deque
from datetime import datetime, timezone
from typing import Any, Callable, Awaitable, Optional, TypeVar

from ._options import VevalOptions
from ._context import VevalExecutionContext
from ._tracing import TraceData, SnapshotData, SnapshotDiff, compare_snapshots, ReplayOptions, ReplayResult
from ._assertions import ITraceAssertion
from ._scenarios import ScenarioItem, ScenarioRunResult, ItemRunResult
from ._http_client import VevalHttpClient
from ._sdk import VevalSdk, _flatten_steps

T = TypeVar("T")


class VevalTestSdk(VevalSdk):
    """
    Test double for VevalSdk. Guarantees no live LLM calls.
    Always reports to the dashboard so runs count toward quota.

    Usage:
        sdk = VevalTestSdk(options).with_replay(trace)
        # inject into your service, call normally.
    """

    def __init__(self, options: VevalOptions):
        super().__init__(options)
        self._replay_trace: Optional[TraceData] = None
        self._last_status: Optional[str] = None
        self._last_error: Optional[str] = None
        self._judge_mocks: dict[str, deque] = {}

    @property
    def last_status(self) -> Optional[str]:
        return self._last_status

    @property
    def last_error(self) -> Optional[str]:
        return self._last_error

    def with_replay(self, trace: TraceData) -> VevalTestSdk:
        self._replay_trace = trace
        return self

    def with_judge_mock(
        self,
        criteria: str,
        passed: bool,
        score: float = 1.0,
        reasoning: str = "",
    ) -> VevalTestSdk:
        """
        Registers a canned judge result for the given criteria string, so scenarios/replays
        using TraceAssert.judge stay deterministic and free in CI. Without a matching mock,
        judge_async raises rather than silently making a real, billed LLM call.
        """
        self._judge_mocks.setdefault(criteria, deque()).append(
            {"score": score, "passed": passed, "reasoning": reasoning}
        )
        return self

    async def judge_async(
        self,
        criteria: str,
        ctx: VevalExecutionContext,
        model: Optional[str] = None,
    ) -> dict:
        queue = self._judge_mocks.get(criteria)
        if queue:
            return queue.popleft()
        raise RuntimeError(
            f"Replay mode: no judge mock for criteria '{criteria}'. "
            "Call with_judge_mock(...) before running a scenario/replay that uses "
            "TraceAssert.judge — this would have made a real, billed LLM call."
        )

    async def run_async(
        self,
        agent_name: str,
        callback: Callable[[VevalExecutionContext], Awaitable[T]],
        input: Any = None,
    ) -> T:
        ctx = VevalExecutionContext(f"tr_{uuid.uuid4().hex}", input)
        if self._replay_trace:
            ctx.load_mock_outputs(self._replay_trace)

        started_at = datetime.now(timezone.utc)
        output: Any = None
        status = "success"
        error: Optional[str] = None

        try:
            output = await callback(ctx)
            self._last_status = "success"
            self._last_error = None
        except Exception as ex:
            status = "error"
            error = str(ex)
            self._last_status = "error"
            self._last_error = error
            raise
        finally:
            completed_at = datetime.now(timezone.utc)
            extra_meta: dict[str, Any] = {"replay": True}
            if self._replay_trace:
                extra_meta["source_trace_id"] = self._replay_trace.trace_id
            payload = self._build_payload(
                ctx.trace_id, agent_name, ctx,
                input, output, status, error,
                started_at, completed_at,
                extra_meta=extra_meta,
            )
            await self._client.send_trace_async(payload)

        return output  # type: ignore[return-value]

    async def get_trace_async(self, trace_id: str) -> Optional[TraceData]:
        if self._replay_trace and self._replay_trace.trace_id == trace_id:
            return self._replay_trace
        return None

    async def load_snapshot_async(self, trace_id: str) -> Optional[SnapshotData]:
        trace = await self.get_trace_async(trace_id)
        return SnapshotData.from_trace(trace) if trace else None

    async def run_scenario_async(
        self,
        scenario_name: str,
        agent: Callable[[VevalExecutionContext], Awaitable[T]],
        scenario_assertions: list[ITraceAssertion],
        items: Optional[list[ScenarioItem]] = None,
    ) -> ScenarioRunResult:
        resolved_items = items or []
        scenario_result = ScenarioRunResult()

        for item in resolved_items:
            effective_assertions = scenario_assertions + list(item.assertions)
            item_result = ItemRunResult(item=item)

            if item.trace_id:
                trace = await self.get_trace_async(item.trace_id)
                if trace is None:
                    item_result.failures.append(
                        f"trace '{item.trace_id}' not found — call with_replay(trace) before run_scenario_async"
                    )
                else:
                    replay_result = await self.replay_async(
                        trace, agent,
                        ReplayOptions(mock_llm_responses=True, assertions=effective_assertions),
                    )
                    item_result.failures.extend(replay_result.failures)
                    item_result.context = replay_result.replayed_context

                    if replay_result.replayed_context:
                        replay_ctx = replay_result.replayed_context
                        payload = self._build_payload(
                            replay_ctx.trace_id, scenario_name, replay_ctx,
                            trace.input, replay_result.output,
                            replay_result.status, replay_result.error,
                            replay_result.started_at, replay_result.completed_at,
                            extra_meta={"replay": True, "source_trace_id": item.trace_id},
                        )
                        await self._client.send_trace_async(payload)

            elif item.input is not None:
                ctx = VevalExecutionContext(f"tr_{uuid.uuid4().hex}", item.input)
                try:
                    await agent(ctx)
                except Exception as ex:
                    item_result.failures.append(f"Agent threw exception: {ex}")

                for assertion in effective_assertions:
                    failure = await assertion.evaluate_async(ctx)
                    if failure:
                        item_result.failures.append(failure)
                item_result.context = ctx
            else:
                item_result.failures.append("ScenarioItem must have either trace_id or input")

            scenario_result.results.append(item_result)

        await self._client.post_scenario_run_async(scenario_name, {
            "passed": scenario_result.passed,
            "pass_count": scenario_result.pass_count,
            "fail_count": scenario_result.fail_count,
            "results": [
                {
                    "name": r.item.name or r.item.trace_id or "item",
                    "passed": r.passed,
                    "failures": r.failures,
                }
                for r in scenario_result.results
            ],
        })

        return scenario_result
