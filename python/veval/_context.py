from __future__ import annotations
import inspect
import json
from typing import Any, Callable, Awaitable, Optional, TypeVar, TYPE_CHECKING
from ._step import Step, StepHandle
from ._fidelity import DIVERGED, LIVE, MATCHED, ReplayBinding, ReplayFidelity, UnusedRecording
from ._snapshots import _canonicalize

if TYPE_CHECKING:
    from ._tracing import StepData, TraceData

_NO_IGNORED_FIELDS: set[str] = set()


def _input_key(value: Any) -> str:
    """Canonical text for matching a call to a recording. The live input is compared in the form it is sent in
    (json.dumps with default=str), so a dataclass or datetime matches what the trace recorded for it."""
    try:
        value = json.loads(json.dumps(value, default=str))
    except (TypeError, ValueError):
        pass
    return _canonicalize(value, _NO_IGNORED_FIELDS)

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
        self._live_model_steps = False
        self._is_replay = False

    @property
    def is_replay(self) -> bool:
        """True when this run replays a recorded trace: replay_async, a trace-backed scenario item, or
        VevalTestSdk.with_replay, mocked or live. Check it before a side effect that isn't inside a step
        served from the recording, such as sending an email; in a live replay, model steps run for real."""
        return self._is_replay

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
        """How each replayed step was bound to the recording; None when the run doesn't replay a recording."""
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
        served, output = self._serve_from_recording(name, input)
        if served:
            return output

        s, handle = self._start_step(name, input)
        try:
            result = await (step(handle) if _takes_handle(step) else step())
            s.complete(result)
            return result
        except Exception as ex:
            s.fail(str(ex))
            raise

    def track_step(self, name: str, input: Any, step: Callable[..., T]) -> T:
        """Synchronous track_step_async, for a step whose work is a plain function. Replays the same way."""
        served, output = self._serve_from_recording(name, input)
        if served:
            return output

        s, handle = self._start_step(name, input)
        try:
            result = _not_awaitable(step(handle) if _takes_handle(step) else step(), "track_step_async")
            s.complete(result)
            return result
        except Exception as ex:
            s.fail(str(ex))
            raise

    def _start_step(self, name: str, input: Any) -> tuple[Step, StepHandle]:
        s = Step(name)
        s.input = input
        self._steps.append(s)
        return s, StepHandle(s)

    def _serve_from_recording(self, name: str, input: Any) -> tuple[bool, Any]:
        """In a replay, binds the call to a recording. Returns (True, recorded output) when the step is served
        from the recording, or (False, None) when it should run."""
        if self._recorded is None:
            return False, None

        recorded, exact = self._find_recording(name, input)
        # In live mode a model step runs for real, including extra calls of a step recorded as a model step.
        recorded_type = recorded.data.type if recorded is not None else self._recorded_type_of(name)
        run_live = self._live_model_steps and recorded_type == "llm"

        if recorded is None and not run_live and self._strict_mock_mode:
            available = ", ".join(dict.fromkeys(r.data.name for r in self._recorded if not r.consumed))
            raise RuntimeError(
                f"Live replay: step '{name}' has no recording to serve, and only steps recorded as 'llm' run live. "
                f"Available steps: {available}. Running it could have reached a real system."
                if self._live_model_steps else
                f"Replay mode: no mock output for step '{name}'. "
                f"Available steps: {available}. "
                "This would have made a real LLM call."
            )
        if recorded is not None:
            recorded.consumed = True

        assert self._fidelity is not None
        if run_live:
            self._fidelity._record(ReplayBinding(
                name, LIVE, recorded.index if recorded else None, recorded.data.input if recorded else None, input,
            ))
            return False, None
        if recorded is None:
            return False, None

        self._fidelity._record(ReplayBinding(
            name, MATCHED if exact else DIVERGED, recorded.index, recorded.data.input, input,
        ))
        mock = recorded.data.output
        s = Step(name)
        s.input = input
        # Keep the recorded type so tool_called and snapshot types behave the same as in the original run.
        s.type = recorded.data.type or "custom"
        s.metadata["_source"] = "replay"
        s.complete(mock)
        self._steps.append(s)
        return True, mock

    def set_metadata(self, key: str, value: Any) -> None:
        self._metadata[key] = value

    def load_mock_outputs(self, trace: TraceData, strict: bool = True, live_model_steps: bool = False) -> None:
        """live_model_steps: run steps recorded as llm for real and serve every other step from the recording."""
        if not trace.steps:
            raise RuntimeError(
                f"Replay trace '{trace.trace_id}' has no steps. Cannot mock LLM calls. "
                "Ensure the trace was recorded with steps before using it for replay."
            )
        self._is_replay = True
        self._strict_mock_mode = strict
        self._live_model_steps = live_model_steps
        self._recorded = [_RecordedStep(step, i) for i, step in enumerate(trace.steps)]
        self._fidelity = ReplayFidelity(UnusedRecording(r.data.name, r.index) for r in self._recorded)

    def _find_recording(self, name: str, input: Any) -> tuple[Optional[_RecordedStep], bool]:
        """Picks the recorded step for a call: among unused recordings with the same name, the one whose input is
        identical (key order ignored), otherwise the earliest — which is reported as diverged."""
        candidates = [r for r in self._recorded or [] if not r.consumed and r.data.name == name]
        if not candidates:
            return None, False

        actual = _input_key(input)
        match = next((r for r in candidates if r.canonical_input == actual), None)
        return match or candidates[0], match is not None

    def _recorded_type_of(self, name: str) -> Optional[str]:
        """The recorded type of a step name, used and unused alike; None when the trace never recorded it."""
        return next((r.data.type for r in self._recorded or [] if r.data.name == name), None)


def _takes_handle(step: Callable[..., Any]) -> bool:
    """Whether the step wants the StepHandle: it has a required parameter. Parameters with defaults don't count, so
    the loop idiom lambda t=t: ... keeps its own value."""
    return any(
        p.default is p.empty and p.kind in (p.POSITIONAL_ONLY, p.POSITIONAL_OR_KEYWORD)
        for p in inspect.signature(step).parameters.values()
    )


def _not_awaitable(value: Any, async_name: str) -> Any:
    """The result of a function given to a synchronous method; an async function there would silently never run."""
    if inspect.isawaitable(value):
        if inspect.iscoroutine(value):
            value.close()
        raise TypeError(f"Veval: got an async function where a plain function is expected; use {async_name} instead.")
    return value


class _RecordedStep:
    def __init__(self, data: StepData, index: int):
        self.data = data
        self.index = index
        self.canonical_input = _input_key(data.input)
        self.consumed = False
