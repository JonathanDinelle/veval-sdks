from __future__ import annotations
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, Optional, TYPE_CHECKING

if TYPE_CHECKING:
    from ._context import VevalExecutionContext
    from ._step import Step


@dataclass
class StepData:
    step_id: str = ""
    parent_step_id: Optional[str] = None
    name: str = ""
    type: str = "custom"
    input: Any = None
    output: Any = None
    status: str = ""
    error: Optional[str] = None
    started_at: Optional[datetime] = None
    completed_at: Optional[datetime] = None
    duration_ms: Optional[int] = None
    cost_usd: Optional[float] = None
    tokens_in: Optional[int] = None
    tokens_out: Optional[int] = None
    model: Optional[str] = None
    metadata: dict[str, Any] = field(default_factory=dict)


@dataclass
class TraceData:
    trace_id: str = ""
    agent_name: str = ""
    project_id: str = ""
    input: Any = None
    output: Any = None
    status: str = "running"
    error: Optional[str] = None
    started_at: Optional[datetime] = None
    completed_at: Optional[datetime] = None
    duration_ms: Optional[int] = None
    total_cost_usd: Optional[float] = None
    metadata: dict[str, Any] = field(default_factory=dict)
    steps: list[StepData] = field(default_factory=list)

    @staticmethod
    def from_dict(data: dict) -> TraceData:
        steps = [
            StepData(
                step_id=s.get("step_id", ""),
                parent_step_id=s.get("parent_step_id"),
                name=s.get("name", ""),
                type=s.get("type", "custom"),
                input=s.get("input"),
                output=s.get("output"),
                status=s.get("status", ""),
                error=s.get("error"),
                started_at=s.get("started_at"),
                completed_at=s.get("completed_at"),
                duration_ms=s.get("duration_ms"),
                cost_usd=s.get("cost_usd"),
                tokens_in=s.get("tokens_in"),
                tokens_out=s.get("tokens_out"),
                model=s.get("model"),
                metadata=s.get("metadata") or {},
            )
            for s in data.get("steps") or []
        ]
        return TraceData(
            trace_id=data.get("trace_id", ""),
            agent_name=data.get("agent_name", ""),
            project_id=data.get("project_id", ""),
            input=data.get("input"),
            output=data.get("output"),
            status=data.get("status", "running"),
            error=data.get("error"),
            started_at=data.get("started_at"),
            completed_at=data.get("completed_at"),
            duration_ms=data.get("duration_ms"),
            total_cost_usd=data.get("total_cost_usd"),
            metadata=data.get("metadata") or {},
            steps=steps,
        )


@dataclass
class SnapshotStep:
    name: str = ""
    output: Any = None


@dataclass
class SnapshotData:
    id: Optional[str] = None
    step_names: list[str] = field(default_factory=list)
    step_order: list[str] = field(default_factory=list)
    step_count: int = 0
    steps: list[SnapshotStep] = field(default_factory=list)

    @staticmethod
    def from_context(ctx: VevalExecutionContext) -> SnapshotData:
        steps: list[SnapshotStep] = []
        _collect_steps(ctx.steps, steps)
        return SnapshotData(
            step_names=list(dict.fromkeys(s.name for s in steps)),
            step_order=[s.name for s in steps],
            step_count=len(steps),
            steps=steps,
        )

    @staticmethod
    def from_trace(trace: TraceData) -> SnapshotData:
        steps = [SnapshotStep(name=s.name, output=s.output) for s in trace.steps]
        return SnapshotData(
            step_names=list(dict.fromkeys(s.name for s in steps)),
            step_order=[s.name for s in steps],
            step_count=len(steps),
            steps=steps,
        )


def _collect_steps(steps: list[Step], result: list[SnapshotStep]) -> None:
    for s in steps:
        result.append(SnapshotStep(name=s.name, output=s.output))
        _collect_steps(s.children, result)


@dataclass
class SnapshotDiff:
    added_steps: list[str] = field(default_factory=list)
    removed_steps: list[str] = field(default_factory=list)
    order_changes: list[str] = field(default_factory=list)

    @property
    def has_changes(self) -> bool:
        return bool(self.added_steps or self.removed_steps or self.order_changes)


def compare_snapshots(snapshot: SnapshotData, ctx: VevalExecutionContext) -> SnapshotDiff:
    current = SnapshotData.from_context(ctx)
    diff = SnapshotDiff()

    snapshot_set = set(snapshot.step_names)
    current_set = set(current.step_names)
    diff.added_steps = list(current_set - snapshot_set)
    diff.removed_steps = list(snapshot_set - current_set)

    min_len = min(len(snapshot.step_order), len(current.step_order))
    for i in range(min_len):
        if snapshot.step_order[i] != current.step_order[i]:
            diff.order_changes.append(
                f"Position {i}: expected '{snapshot.step_order[i]}', got '{current.step_order[i]}'"
            )

    return diff


@dataclass
class ReplayOptions:
    mock_llm_responses: bool = False
    assertions: list[Any] = field(default_factory=list)


@dataclass
class ReplayResult:
    failures: list[str] = field(default_factory=list)
    replayed_context: Optional[VevalExecutionContext] = None
    output: Any = None
    started_at: Optional[datetime] = None
    completed_at: Optional[datetime] = None
    status: str = "success"
    error: Optional[str] = None

    @property
    def passed(self) -> bool:
        return len(self.failures) == 0
