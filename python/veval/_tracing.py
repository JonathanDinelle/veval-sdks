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
from ._fidelity import ReplayFidelity, ReplayStepRule, ReplayStop  # noqa: E402
from ._faults import FaultReport, ReplayFault  # noqa: E402
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
    # Per step name: how calls match their recordings (name only, or with some input fields ignored) and, in a
    # live replay, whether a diverged call stops the run or is served. Matching rules apply to mocked replays too.
    step_rules: dict[str, ReplayStepRule] = field(default_factory=dict)
    # Live replay: steps recorded with one of these types stop the run when a call diverges, unless their
    # step_rules entry says otherwise. A stopped replay is inconclusive (ReplayResult.inconclusive): past that step
    # the recording can't say what the real tool would have returned. Defaults to {"tool"}; other diverged steps are
    # served the next unused recording and reported in ReplayResult.fidelity.
    stop_on_divergence_types: set[str] = field(default_factory=lambda: {"tool"})
    # Failures to serve instead of recorded outputs: a timeout, an HTTP error, a rate limit, a malformed output or a
    # custom exception (ReplayFault), so the replay runs the agent's retries, fallbacks and guards. Needs a mocked or
    # live replay. Once a fault has fired, a call that matches no unused recording stops the replay inconclusive
    # instead of being served a recording from another path; in a live replay, model steps still run live, so the
    # model's reaction to the error is tested. A fault that never fires fails the replay.
    faults: list[ReplayFault] = field(default_factory=list)


@dataclass
class ReplayResult:
    failures: list[str] = field(default_factory=list)
    replayed_context: Optional[VevalExecutionContext] = None
    output: Any = None
    started_at: Optional[datetime] = None
    completed_at: Optional[datetime] = None
    # The replay trace's status: "success", "error" (the agent raised), or "inconclusive" (the replay stopped).
    status: str = "success"
    error: Optional[str] = None
    # How the replay differed from its recording; set when compare_with_recording is on.
    recording_diff: Optional[SnapshotDiff] = None
    # How each call was bound to the recording; set for mocked and live replays (mock_llm_responses or live_model_steps).
    fidelity: Optional[ReplayFidelity] = None
    # Where a live replay stopped because a call left the recording, or None. When set, assertions were not evaluated
    # and failures is empty: the replay can't back up a pass or a fail.
    inconclusive: Optional[ReplayStop] = None

    @property
    def outcome(self) -> str:
        """How the replay ended: "passed", "failed", or "inconclusive" (a live replay left its recording and stopped)."""
        if self.inconclusive is not None:
            return "inconclusive"
        return "passed" if not self.failures else "failed"

    @property
    def passed(self) -> bool:
        """True only when the replay passed; False when it failed or was inconclusive."""
        return self.outcome == "passed"

    @property
    def faults(self) -> list[FaultReport]:
        """Each fault in ReplayOptions.faults and how many times it fired, in declared order."""
        return self.fidelity.faults if self.fidelity is not None else []

    @property
    def summary(self) -> str:
        """One line, e.g. "passed — 40/64 recorded steps used, 3 diverged but served, 12 live, stayed on path"."""
        from ._summary import replay_summary
        return replay_summary(self.failures, self.inconclusive, self.fidelity)
