from __future__ import annotations
import inspect
from typing import Any, Callable, Awaitable, Optional, TypeVar, TYPE_CHECKING
from ._step import Step, StepHandle
from ._fidelity import DIVERGED, MATCHED, ReplayBinding, ReplayFidelity, UnusedRecording
from ._snapshots import _canonicalize

if TYPE_CHECKING:
    from ._tracing import StepData, TraceData

_NO_IGNORED_FIELDS: set[str] = set()

T = TypeVar("T")


class VevalExecutionContext:
    def __init__(self, trace_id: str, input: Any = None):
        self.trace_id = trace_id
        self.input = input
        self._steps: list[Step] = []
        self._metadata: dict[str, Any] = {}
        self._judgments: list[dict] = []
        self._recorded: Optional[list[_RecordedStep]] = None
        self._fidelity: Optional[ReplayFidelity] = None
        self._strict_mock_mode = False

    @property
    def steps(self) -> list[Step]:
        return self._steps

    @property
    def trace_meta(self) -> dict[str, Any]:
        return self._metadata

    @property
    def judgments(self) -> list[dict]:
        return self._judgments

    @property
    def replay_fidelity(self) -> Optional[ReplayFidelity]:
        """How each replayed step was bound to the recording; None when the run is not a mocked replay."""
        return self._fidelity

    def record_judgment(self, criteria: str, score: float, passed: bool, reasoning: str) -> None:
        self._judgments.append({
            "criteria": criteria,
            "score": score,
            "passed": passed,
            "reasoning": reasoning,
        })

    async def track_step_async(
        self,
        name: str,
        input: Any,
        step: Callable[..., Awaitable[T]],
    ) -> T:
        if self._recorded is not None:
            recorded = self._bind_recorded_step(name, input)
            if recorded is None and self._strict_mock_mode:
                available = ", ".join(dict.fromkeys(r.data.name for r in self._recorded if not r.consumed))
                raise RuntimeError(
                    f"Replay mode: no mock output for step '{name}'. "
                    f"Available steps: {available}. "
                    "This would have made a real LLM call."
                )
            if recorded is not None:
                mock = recorded.data.output
                s = Step(name)
                s.input = input
                # Keep the recorded type so tool_called and snapshot types behave the same as in the original run.
                s.type = recorded.data.type or "custom"
                s.metadata["_source"] = "replay"
                s.complete(mock)
                self._steps.append(s)
                return mock  # type: ignore[return-value]

        s = Step(name)
        s.input = input
        self._steps.append(s)
        handle = StepHandle(s)

        try:
            sig = inspect.signature(step)
            if len(sig.parameters) > 0:
                result = await step(handle)
            else:
                result = await step()  # type: ignore[call-arg]
            s.complete(result)
            return result
        except Exception as ex:
            s.fail(str(ex))
            raise

    def set_metadata(self, key: str, value: Any) -> None:
        self._metadata[key] = value

    def load_mock_outputs(self, trace: TraceData, strict: bool = True) -> None:
        if not trace.steps:
            raise RuntimeError(
                f"Replay trace '{trace.trace_id}' has no steps. Cannot mock LLM calls. "
                "Ensure the trace was recorded with steps before using it for replay."
            )
        self._strict_mock_mode = strict
        self._recorded = [_RecordedStep(step, i) for i, step in enumerate(trace.steps)]
        self._fidelity = ReplayFidelity(UnusedRecording(r.data.name, r.index) for r in self._recorded)

    def _bind_recorded_step(self, name: str, input: Any) -> Optional[_RecordedStep]:
        """Picks the recorded step to serve for a call: among unused recordings with the same name, the one whose
        input is identical (key order ignored), otherwise the earliest — which is reported as diverged."""
        candidates = [r for r in self._recorded or [] if not r.consumed and r.data.name == name]
        if not candidates:
            return None

        actual = _canonicalize(input, _NO_IGNORED_FIELDS)
        exact = next((r for r in candidates if r.canonical_input == actual), None)
        recorded = exact or candidates[0]
        recorded.consumed = True

        assert self._fidelity is not None
        self._fidelity._record(ReplayBinding(
            name, MATCHED if exact else DIVERGED, recorded.index, recorded.data.input, input,
        ))
        return recorded


class _RecordedStep:
    def __init__(self, data: StepData, index: int):
        self.data = data
        self.index = index
        self.canonical_input = _canonicalize(data.input, _NO_IGNORED_FIELDS)
        self.consumed = False
