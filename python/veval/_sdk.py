from __future__ import annotations
import asyncio
import inspect
import uuid
from datetime import datetime, timezone
from typing import Any, Awaitable, Callable, Coroutine, Optional, TypeVar, Union

from ._options import VevalOptions
from ._context import VevalExecutionContext
from ._step import Step
from ._tracing import (
    TraceData, SnapshotData, SnapshotDiff, SnapshotOptions, compare_snapshots,
    ReplayOptions, ReplayResult,
)
from ._assertions import ITraceAssertion
from ._scenarios import ScenarioItem, ScenarioRunResult, ItemRunResult
from ._http_client import VevalHttpClient
from ._scenario_report import ScenarioReport
from ._snapshots import save_from_context_payload, save_from_trace_payload, snapshot_run_payload

T = TypeVar("T")


class VevalSdk:
    def __init__(self, options: VevalOptions):
        self._options = options
        self._client = VevalHttpClient(options.api_key, options._endpoint, options.raise_on_send_failure)

    async def run_async(
        self,
        agent_name: str,
        callback: Callable[[VevalExecutionContext], Union[Awaitable[T], T]],
        input: Any = None,
    ) -> T:
        """Runs the agent and sends its trace. callback may be a coroutine function or a plain function."""
        ctx = self._new_run_context(input)
        started_at = datetime.now(timezone.utc)
        try:
            result = await _call_agent(callback, ctx)
            self._check_run(ctx)
        except Exception as ex:
            self._run_finished(str(ex))
            # The agent's own exception takes priority over a failed send (which is still logged).
            await self._client.send_trace_async(
                self._run_payload(agent_name, ctx, None, str(ex), started_at), raise_on_failure=False
            )
            raise

        self._run_finished(None)
        await self._client.send_trace_async(self._run_payload(agent_name, ctx, result, None, started_at))
        return result

    def run(
        self,
        agent_name: str,
        callback: Callable[[VevalExecutionContext], T],
        input: Any = None,
    ) -> T:
        """Synchronous run_async, for an agent that is a plain function. No event loop is involved, so it is safe to
        call from any thread. Track steps inside it with ctx.track_step."""
        ctx = self._new_run_context(input)
        started_at = datetime.now(timezone.utc)
        try:
            result = callback(ctx)
            self._check_run(ctx)
        except Exception as ex:
            self._run_finished(str(ex))
            # The agent's own exception takes priority over a failed send (which is still logged).
            self._client.send_trace(self._run_payload(agent_name, ctx, None, str(ex), started_at), raise_on_failure=False)
            raise

        self._run_finished(None)
        self._client.send_trace(self._run_payload(agent_name, ctx, result, None, started_at))
        return result

    def _new_run_context(self, input: Any) -> VevalExecutionContext:
        return VevalExecutionContext(f"tr_{uuid.uuid4().hex}", input)

    def _check_run(self, ctx: VevalExecutionContext) -> None:
        """Raises when a finished run must count as failed; VevalTestSdk uses it for unused recordings."""

    def _run_finished(self, error: Optional[str]) -> None:
        """Called once per run with its error, or None on success."""

    def _run_extra_meta(self) -> Optional[dict]:
        return None

    def _run_payload(
        self, agent_name: str, ctx: VevalExecutionContext, output: Any, error: Optional[str], started_at: datetime,
    ) -> dict:
        return self._build_payload(
            ctx.trace_id, agent_name, ctx, ctx.input, output,
            "error" if error is not None else "success", error, started_at, datetime.now(timezone.utc),
            extra_meta=self._run_extra_meta(),
        )

    def get_trace(self, trace_id: str) -> Optional[TraceData]:
        """Synchronous get_trace_async."""
        return self._client.get_trace(trace_id)

    async def get_trace_async(self, trace_id: str) -> Optional[TraceData]:
        return await self._client.get_trace_async(trace_id)

    async def judge_async(
        self,
        criteria: str,
        ctx: VevalExecutionContext,
        model: Optional[str] = None,
        threshold: Optional[float] = None,
        reference_output: Optional[Any] = None,
        samples: Optional[int] = None,
    ) -> dict:
        last_step = ctx.steps[-1] if ctx.steps else None
        payload = {
            "criteria": criteria,
            "input": last_step.input if last_step else ctx.input,
            "output": last_step.output if last_step else None,
            "model": model,
            "threshold": threshold,
            "reference_output": reference_output,
            "samples": samples,
        }
        return await self._client.judge_async(payload)

    async def load_snapshot_async(self, trace_id: str) -> Optional[SnapshotData]:
        trace = await self._client.get_trace_async(trace_id)
        return SnapshotData.from_trace(trace) if trace else None

    async def save_snapshot_async(
        self,
        snapshot_name: str,
        source: Union[str, VevalExecutionContext],
    ) -> SnapshotData:
        """
        Stores a named baseline — from a recorded trace ID (the trace is pinned so retention never
        deletes it), or from a run you just executed. Saving again under the same name replaces it.
        """
        payload = (
            save_from_trace_payload(snapshot_name, source)
            if isinstance(source, str)
            else save_from_context_payload(snapshot_name, source)
        )
        return await self._client.create_snapshot_async(payload)

    async def get_snapshot_async(self, snapshot_name: str) -> Optional[SnapshotData]:
        """The latest stored baseline with this name, or None if none exists."""
        return await self._client.get_snapshot_async(snapshot_name)

    async def compare_snapshot_async(
        self,
        snapshot_name: str,
        snapshot: SnapshotData,
        ctx: VevalExecutionContext,
        options: Optional[SnapshotOptions] = None,
    ) -> SnapshotDiff:
        """Compares a run against a baseline and records the result in the dashboard."""
        diff = compare_snapshots(snapshot, ctx, options)
        await self._client.post_scenario_run_async(snapshot_name, snapshot_run_payload(snapshot_name, diff))
        return diff

    async def replay_async(
        self,
        trace: TraceData,
        callback: Callable[[VevalExecutionContext], Union[Awaitable[T], T]],
        options: Optional[ReplayOptions] = None,
    ) -> ReplayResult:
        """callback may be a coroutine function or a plain function."""
        options = options or ReplayOptions()
        ctx = _replay_context(trace, options)
        started_at = datetime.now(timezone.utc)
        output: Any = None
        error: Optional[str] = None
        try:
            output = await _call_agent(callback, ctx)
        except Exception as ex:
            error = str(ex)

        completed_at = datetime.now(timezone.utc)
        assertion_failures = await _evaluate(options.assertions, ctx)
        return _replay_result(trace, options, ctx, output, error, started_at, completed_at, assertion_failures)

    def replay(
        self,
        trace: TraceData,
        callback: Callable[[VevalExecutionContext], T],
        options: Optional[ReplayOptions] = None,
    ) -> ReplayResult:
        """Synchronous replay_async, for an agent that is a plain function. The agent runs outside any event loop;
        assertions are evaluated after it on a private one, so call this from synchronous code."""
        options = options or ReplayOptions()
        ctx = _replay_context(trace, options)
        started_at = datetime.now(timezone.utc)
        output: Any = None
        error: Optional[str] = None
        try:
            output = callback(ctx)
        except Exception as ex:
            error = str(ex)

        completed_at = datetime.now(timezone.utc)
        assertion_failures = _run_sync(_evaluate(options.assertions, ctx), "replay_async") if options.assertions else []
        return _replay_result(trace, options, ctx, output, error, started_at, completed_at, assertion_failures)

    def run_scenario(
        self,
        scenario_name: str,
        agent: Callable[[VevalExecutionContext], T],
        scenario_assertions: list[ITraceAssertion],
        items: Optional[list[ScenarioItem]] = None,
        mock_model_steps: bool = False,
    ) -> ScenarioRunResult:
        """Synchronous run_scenario_async, for an agent that is a plain function. The scenario runs on a private
        event loop, so call this from synchronous code."""
        return _run_sync(
            self.run_scenario_async(scenario_name, agent, scenario_assertions, items, mock_model_steps),
            "run_scenario_async",
        )

    async def run_scenario_async(
        self,
        scenario_name: str,
        agent: Callable[[VevalExecutionContext], Union[Awaitable[T], T]],
        scenario_assertions: list[ITraceAssertion],
        items: Optional[list[ScenarioItem]] = None,
        mock_model_steps: bool = False,
    ) -> ScenarioRunResult:
        """agent may be a coroutine function or a plain function. mock_model_steps replays every trace-backed item fully mocked, as if each set ScenarioItem.mock_model_steps;
        it is the switch for items loaded from the dashboard."""
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
        report = ScenarioReport(self._client, scenario_name)

        for item in resolved_items:
            effective_assertions = scenario_assertions + list(item.assertions)
            item_result = ItemRunResult(item=item)

            if item.trace_id:
                trace = await self.get_trace_async(item.trace_id)
                if trace is None:
                    item_result.failures.append(_missing_trace_message(item.trace_id))
                else:
                    replay_result = await self.replay_async(
                        trace, agent,
                        _scenario_replay_options(item, mock_model_steps, effective_assertions),
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
                        await report.send_trace_async(payload)

            elif item.input is not None:
                ctx = await self._run_and_capture_context(scenario_name, agent, item.input, report)
                item_result.failures.extend(await _evaluate(effective_assertions, ctx))
                item_result.context = ctx
            else:
                item_result.failures.append("ScenarioItem must have either trace_id or input")

            scenario_result.results.append(item_result)

        await report.complete_async(scenario_result, unnamed_item="synthetic")
        return scenario_result

    async def _run_and_capture_context(
        self,
        agent_name: str,
        agent: Callable[[VevalExecutionContext], Union[Awaitable[T], T]],
        input: Any,
        report: ScenarioReport,
    ) -> VevalExecutionContext:
        """Runs one synthetic scenario item, sends its trace through the report, and returns its context."""
        trace_id = f"tr_{uuid.uuid4().hex}"
        ctx = VevalExecutionContext(trace_id, input)
        started_at = datetime.now(timezone.utc)

        output: Any = None
        status = "success"
        error: Optional[str] = None
        try:
            output = await _call_agent(agent, ctx)
        except Exception as ex:
            # Not rethrown: assertions see the error through the step statuses.
            status = "error"
            error = str(ex)

        completed_at = datetime.now(timezone.utc)
        payload = self._build_payload(
            trace_id, agent_name, ctx, input, output,
            status, error, started_at, completed_at,
        )
        await report.send_trace_async(payload)
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


async def _call_agent(agent: Callable[[VevalExecutionContext], Any], ctx: VevalExecutionContext) -> Any:
    """Calls an agent that is either a coroutine function or a plain function."""
    result = agent(ctx)
    return await result if inspect.isawaitable(result) else result


async def _evaluate(assertions: list, ctx: VevalExecutionContext) -> list[str]:
    failures: list[str] = []
    for assertion in assertions:
        failure = await assertion.evaluate_async(ctx)
        if failure:
            failures.append(failure)
    return failures


def _run_sync(coro: Coroutine[Any, Any, T], async_name: str) -> T:
    """Runs async SDK work for a synchronous method on a private event loop."""
    try:
        asyncio.get_running_loop()
    except RuntimeError:
        return asyncio.run(coro)
    coro.close()
    raise RuntimeError(f"Veval: called from a running event loop; use `await {async_name}(...)` instead.")


def _replay_context(trace: TraceData, options: ReplayOptions) -> VevalExecutionContext:
    ctx = VevalExecutionContext(f"tr_{uuid.uuid4().hex}", trace.input)
    ctx._is_replay = True
    if options.live_model_steps:
        ctx.load_mock_outputs(trace, live_model_steps=True)
    elif options.mock_llm_responses:
        ctx.load_mock_outputs(trace)
    return ctx


def _replay_result(
    trace: TraceData,
    options: ReplayOptions,
    ctx: VevalExecutionContext,
    output: Any,
    error: Optional[str],
    started_at: datetime,
    completed_at: datetime,
    assertion_failures: list[str],
) -> ReplayResult:
    failures: list[str] = []
    if error:
        failures.append(f"Replay threw exception: {error}")
    fidelity = ctx.replay_fidelity
    binding_failure = None if error or fidelity is None else fidelity._failure(trace.trace_id, options.live_model_steps)
    if binding_failure:
        failures.append(binding_failure)
    failures.extend(assertion_failures)

    recording_diff = None
    if options.compare_with_recording is not None:
        recording_diff = compare_snapshots(SnapshotData.from_trace(trace), ctx, options.compare_with_recording)
        if recording_diff.has_changes:
            failures.append(
                "Replay drifted from its recording — " + recording_diff.summary(f"recording {trace.trace_id}")
            )

    return ReplayResult(
        failures=failures,
        replayed_context=ctx,
        recording_diff=recording_diff,
        fidelity=fidelity,
        output=output,
        started_at=started_at,
        completed_at=completed_at,
        status="error" if error else "success",
        error=error,
    )


def _scenario_replay_options(item: ScenarioItem, mock_model_steps: bool, assertions: list) -> ReplayOptions:
    """How a trace-backed item replays: model steps live and everything else from the recording, unless the item or
    the whole scenario opts into mocked replay."""
    mocked = mock_model_steps or item.mock_model_steps
    return ReplayOptions(mock_llm_responses=mocked, live_model_steps=not mocked, assertions=assertions)


def _missing_trace_message(trace_id: str) -> str:
    """Failure for a trace-backed scenario item whose trace the API returned 404 for. The usual cause is
    retention deleting a trace that is only referenced from test code."""
    return (
        f"trace '{trace_id}' not found — it may have expired under your plan's retention, or the ID or workspace is wrong. "
        "Pin it on the trace page in the dashboard to keep it."
    )


def _flatten_steps(steps: list[Step]) -> list[Step]:
    result: list[Step] = []
    for s in steps:
        result.append(s)
        result.extend(_flatten_steps(s.children))
    return result
