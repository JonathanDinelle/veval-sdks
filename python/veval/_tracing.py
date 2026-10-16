from __future__ import annotations
from dataclasses import dataclass, field
from datetime import datetime
from typing import Any, Optional, TYPE_CHECKING

if TYPE_CHECKING:
    from ._context import VevalExecutionContext



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


# Snapshot types live in _snapshots; re-exported here for existing imports.
from ._fidelity import ReplayFidelity  # noqa: E402
from ._snapshots import (  # noqa: E402
    SnapshotStep,
    SnapshotData,
    SnapshotDiff,
    SnapshotOptions,
    compare_snapshots,
)


@dataclass
class ReplayOptions:
    # Serve every step's recorded output instead of running it. Deterministic and free, so it checks control
    # flow and code changes; it can't detect a prompt regression, because model calls return the old outputs.
    mock_llm_responses: bool = False
    assertions: list[Any] = field(default_factory=list)
    # When set, the replay also fails if its step sequence or step inputs drifted from the recorded
    # trace. Without it, a call whose input differs from every recording of that step is still served
    # (the next unused recording in order) and only reported in ReplayResult.fidelity.
    compare_with_recording: Optional[SnapshotOptions] = None
    # Run steps recorded as llm for real and serve every other step (tools, untyped steps) from the
    # recording, so a prompt change shows up while tools never reach a real system. Takes precedence over
    # mock_llm_responses. The replay fails if no step ran live.
    live_model_steps: bool = False


@dataclass
class ReplayResult:
    failures: list[str] = field(default_factory=list)
    replayed_context: Optional[VevalExecutionContext] = None
    output: Any = None
    started_at: Optional[datetime] = None
    completed_at: Optional[datetime] = None
    status: str = "success"
    error: Optional[str] = None
    # How the replay differed from its recording; set when compare_with_recording is on.
    recording_diff: Optional[SnapshotDiff] = None
    # How each call was bound to the recording; set for mocked and live replays (mock_llm_responses or live_model_steps).
    fidelity: Optional[ReplayFidelity] = None

    @property
    def passed(self) -> bool:
        return len(self.failures) == 0
