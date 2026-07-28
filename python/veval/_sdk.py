from __future__ import annotations
import uuid
from datetime import datetime, timezone
from typing import Any, Callable, Awaitable, Optional, TypeVar

from ._options import VevalOptions
from ._context import VevalExecutionContext
from ._step import Step
from ._tracing import (
    TraceData, SnapshotData, SnapshotDiff, compare_snapshots,
    ReplayOptions, ReplayResult,
)
from ._assertions import ITraceAssertion
from ._scenarios import ScenarioItem, ScenarioRunResult, ItemRunResult
from ._http_client import VevalHttpClient

T = TypeVar("T")


class VevalSdk:
    def __init__(self, options: VevalOptions):
        self._options = options
        self._client = VevalHttpClient(options.api_key, options.endpoint)

    async def run_async(
        self,
        agent_name: str,
        callback: Callable[[VevalExecutionContext], Awaitable[T]],
        input: Any = None,
    ) -> T:
        trace_id = f"tr_{uuid.uuid4().hex}"
        ctx = VevalExecutionContext(trace_id, input)
        started_at = datetime.now(timezone.utc)

        try:
            result = await callback(ctx)
            completed_at = datetime.now(timezone.utc)
            payload = self._build_payload(
                trace_id, agent_name, ctx, input, result,
                "success", None, started_at, completed_at,
            )
            await self._client.send_trace_async(payload)
            return result
        except Exception as ex:
            completed_at = datetime.now(timezone.utc)
            payload = self._build_payload(
                trace_id, agent_name, ctx, input, None,
                "error", str(ex), started_at, completed_at,
            )
            await self._client.send_trace_async(payload)
            raise

    async def get_trace_async(self, trace_id: str) -> Optional[TraceData]:
        return await self._client.get_trace_async(trace_id)

    async def judge_async(
        self,
        criteria: str,
        ctx: VevalExecutionContext,
        model: Optional[str] = None,
    ) -> dict:
        last_step = ctx.steps[-1] if ctx.steps else None
        payload = {
            "criteria": criteria,
            "input": last_step.input if last_step else ctx.input,
            "output": last_step.output if last_step else None,
            "model": model,
        }
        return await self._client.judge_async(payload)

    async def load_snapshot_async(self, trace_id: str) -> Optional[SnapshotData]:
        trace = await self._client.get_trace_async(trace_id)
        return SnapshotData.from_trace(trace) if trace else None

    async def compare_snapshot_async(
        self,
        snapshot_name: str,
        snapshot: SnapshotData,
        ctx: VevalExecutionContext,
    ) -> SnapshotDiff:
        diff = compare_snapshots(snapshot, ctx)
        actual = SnapshotData.from_context(ctx)
        await self._client.post_scenario_run_async(snapshot_name, {
            "passed": not diff.has_changes,
            "pass_count": 0 if diff.has_changes else 1,
            "fail_count": 1 if diff.has_changes else 0,
            "results": [{
                "name": snapshot_name,
                "passed": not diff.has_changes,
                "type": "snapshot",
                "expected": [{"name": s.name, "output": s.output} for s in snapshot.steps],
                "actual": [{"name": s.name, "output": s.output} for s in actual.steps],
                "failures": (
                    [f"added: {s}" for s in diff.added_steps]
                    + [f"removed: {s}" for s in diff.removed_steps]
                    + diff.order_changes
                ),
            }],
        })
        return diff

    async def replay_async(
        self,
        trace: TraceData,
        callback: Callable[[VevalExecutionContext], Awaitable[T]],
        options: Optional[ReplayOptions] = None,
    ) -> ReplayResult:
        options = options or ReplayOptions()
        ctx = VevalExecutionContext(f"tr_{uuid.uuid4().hex}", trace.input)
        if options.mock_llm_responses:
            ctx.load_mock_outputs(trace)

        started_at = datetime.now(timezone.utc)
        output: Any = None
        status = "success"
        error: Optional[str] = None

        try:
            output = await callback(ctx)
        except Exception as ex:
            status = "error"
            error = str(ex)

        completed_at = datetime.now(timezone.utc)
        failures: list[str] = []
        if error:
            failures.append(f"Replay threw exception: {error}")
        for assertion in options.assertions:
            failure = await assertion.evaluate_async(ctx)
            if failure:
                failures.append(failure)

        return ReplayResult(
            failures=failures,
            replayed_context=ctx,
            output=output,
            started_at=started_at,
            completed_at=completed_at,
            status=status,
            error=error,
        )

    async def run_scenario_async(
        self,
        scenario_name: str,
        agent: Callable[[VevalExecutionContext], Awaitable[T]],
        scenario_assertions: list[ITraceAssertion],
        items: Optional[list[ScenarioItem]] = None,
    ) -> ScenarioRunResult:
        if items is None:
            raw_items = await self._client.get_scenario_items_async(scenario_name)
            resolved_items = [
                ScenarioItem(
                    name=i.get("name"),
                    trace_id=i.get("trace_id"),
                    input=i.get("input"),
                )
                for i in raw_items
            ]
        else:
            resolved_items = items

        scenario_result = ScenarioRunResult()

        for item in resolved_items:
            effective_assertions = scenario_assertions + list(item.assertions)
            item_result = ItemRunResult(item=item)

            if item.trace_id:
                trace = await self.get_trace_async(item.trace_id)
                if trace is None:
                    item_result.failures.append(f"trace '{item.trace_id}' not found")
                else:
                    replay_result = await self.replay_async(
                        trace, agent,
                        ReplayOptions(mock_llm_responses=True, assertions=effective_assertions),
                    )
                    item_result.failures.extend(replay_result.failures)
                    item_result.context = replay_result.replayed_context

                    replay_ctx = replay_result.replayed_context
                    if replay_ctx:
                        payload = self._build_payload(
                            replay_ctx.trace_id, scenario_name, replay_ctx,
                            trace.input, replay_result.output,
                            replay_result.status, replay_result.error,
                            replay_result.started_at, replay_result.completed_at,
                            extra_meta={"replay": True, "source_trace_id": item.trace_id},
                        )
                        await self._client.send_trace_async(payload)

            elif item.input is not None:
                ctx = await self._run_and_capture_context(scenario_name, agent, item.input)
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
                    "name": r.item.name or r.item.trace_id or "synthetic",
                    "passed": r.passed,
                    "failures": r.failures,
                }
                for r in scenario_result.results
            ],
        })

        return scenario_result

    async def _run_and_capture_context(
        self,
        agent_name: str,
        agent: Callable[[VevalExecutionContext], Awaitable[T]],
        input: Any,
    ) -> VevalExecutionContext:
        trace_id = f"tr_{uuid.uuid4().hex}"
        ctx = VevalExecutionContext(trace_id, input)
        started_at = datetime.now(timezone.utc)

        try:
            result = await agent(ctx)
            completed_at = datetime.now(timezone.utc)
            payload = self._build_payload(
                trace_id, agent_name, ctx, input, result,
                "success", None, started_at, completed_at,
            )
            await self._client.send_trace_async(payload)
        except Exception as ex:
            completed_at = datetime.now(timezone.utc)
            payload = self._build_payload(
                trace_id, agent_name, ctx, input, None,
                "error", str(ex), started_at, completed_at,
            )
            await self._client.send_trace_async(payload)

        return ctx

    def _build_payload(
        self,
        trace_id: str,
        agent_name: str,
        ctx: VevalExecutionContext,
        input: Any,
        output: Any,
        status: str,
        error: Optional[str],
        started_at: datetime,
        completed_at: datetime,
        extra_meta: Optional[dict] = None,
    ) -> dict:
        steps = _flatten_steps(ctx.steps)
        duration_ms = int((completed_at - started_at).total_seconds() * 1000)
        total_cost = sum(s.cost_usd or 0 for s in steps)

        metadata = dict(ctx.trace_meta)
        if extra_meta:
            metadata.update(extra_meta)

        return {
            "trace_id": trace_id,
            "agent_name": agent_name,
            "input": input,
            "output": output,
            "status": status,
            "error": error,
            "started_at": started_at.isoformat(),
            "completed_at": completed_at.isoformat(),
            "duration_ms": duration_ms,
            "total_cost_usd": total_cost or None,
            "metadata": metadata,
            "steps": [
                {
                    "step_id": s.step_id,
                    "parent_step_id": s.parent_step_id,
                    "name": s.name,
                    "type": s.type,
                    "input": s.input,
                    "output": s.output,
                    "status": s.status,
                    "error": s.error,
                    "started_at": s.started_at.isoformat(),
                    "completed_at": s.completed_at.isoformat() if s.completed_at else None,
                    "duration_ms": s.duration_ms,
                    "cost_usd": s.cost_usd,
                    "tokens_in": s.tokens_in,
                    "tokens_out": s.tokens_out,
                    "model": s.model,
                    "metadata": s.metadata,
                }
                for s in steps
            ],
        }


def _flatten_steps(steps: list[Step]) -> list[Step]:
    result: list[Step] = []
    for s in steps:
        result.append(s)
        result.extend(_flatten_steps(s.children))
    return result
