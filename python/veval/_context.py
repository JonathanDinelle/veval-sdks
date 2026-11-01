from __future__ import annotations
import inspect
import json
from typing import Any, Callable, Awaitable, Optional, TypeVar, TYPE_CHECKING
from ._step import Step, StepHandle
from ._fidelity import (
    DIVERGED, LIVE, MATCHED, STOP, STOP_DIVERGED, STOP_NO_RECORDING,
    ReplayBinding, ReplayFidelity, ReplayStepRule, ReplayStop, ReplayStoppedError, UnusedRecording,
)
from ._snapshots import _canonicalize

if TYPE_CHECKING:
    from ._tracing import StepData, TraceData

_NO_IGNORED_FIELDS: set[str] = set()


def _input_key(value: Any, ignore_fields: set[str] = _NO_IGNORED_FIELDS) -> str:
    """Canonical text for matching a call to a recording. The live input is compared in the form it is sent in
    (json.dumps with default=str), so a dataclass or datetime matches what the trace recorded for it."""
    try:
        value = json.loads(json.dumps(value, default=str))
    except (TypeError, ValueError):
        pass
    return _canonicalize(value, ignore_fields)


def _ignoring_key(value: Any, ignore_fields: tuple[str, ...]) -> str:
    """The matching key of an input with some fields dropped. An input that is a JSON object or array encoded as a
    string, such as a model's tool-call arguments, is parsed first, so its fields can be dropped too."""
    if isinstance(value, str) and value.lstrip()[:1] in ("{", "["):
        try:
            return _input_key(json.loads(value), set(ignore_fields))
        except ValueError:
            pass  # not JSON after all: compare the text
    return _input_key(value, set(ignore_fields))


def _one_line(value: Any) -> str:
    """An input as compact JSON for a message, as the C# and Node SDKs print it."""
    try:
        return json.dumps(value, default=str, ensure_ascii=False, separators=(",", ":"))
    except (TypeError, ValueError):
        return str(value)

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
        self._step_rules: dict[str, ReplayStepRule] = {}
        self._stop_on_divergence_types: set[str] = set()
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
        assert self._fidelity is not None
        # Once a live replay has stopped, nothing after it can be served truthfully, even if the agent caught the stop.
        if self._fidelity.stop is not None:
            raise ReplayStoppedError(self._fidelity.stop)

        rule = self._step_rules.get(name)
        if rule is not None and not isinstance(rule, ReplayStepRule):
            raise TypeError(f"step_rules['{name}'] must be a ReplayStepRule, not {type(rule).__name__}")
        recorded, exact = self._find_recording(name, input, rule)
        # In live mode a model step runs for real, including extra calls of a step recorded as a model step.
        recorded_type = recorded.data.type if recorded is not None else self._recorded_type_of(name)
        run_live = self._live_model_steps and recorded_type == "llm"

        if recorded is None and not run_live and self._strict_mock_mode:
            if self._live_model_steps:
                raise self._stop_at(name, STOP_NO_RECORDING, None, input, recorded_type)
            available = ", ".join(dict.fromkeys(r.data.name for r in self._recorded if not r.consumed))
            raise RuntimeError(
                f"Replay mode: no mock output for step '{name}'. "
                f"Available steps: {available}. "
                "This would have made a real LLM call."
            )

        # A live model handed a recording from another path would reason over a page it never reached.
        if (self._live_model_steps and not run_live and recorded is not None and not exact
                and self._stops_on_divergence(rule, recorded.data.type)):
            raise self._stop_at(name, STOP_DIVERGED, recorded.data.input, input, recorded_type)

        if recorded is not None:
            recorded.consumed = True

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

    def load_mock_outputs(
        self,
        trace: TraceData,
        strict: bool = True,
        live_model_steps: bool = False,
        step_rules: Optional[dict[str, ReplayStepRule]] = None,
        stop_on_divergence_types: Optional[set[str]] = None,
    ) -> None:
        """live_model_steps: run steps recorded as llm for real and serve every other step from the recording.
        step_rules and stop_on_divergence_types: step matching rules, and which diverged steps stop a live replay."""
        if not trace.steps:
            raise RuntimeError(
                f"Replay trace '{trace.trace_id}' has no steps. Cannot mock LLM calls. "
                "Ensure the trace was recorded with steps before using it for replay."
            )
        self._is_replay = True
        self._strict_mock_mode = strict
        self._live_model_steps = live_model_steps
        self._step_rules = dict(step_rules or {})
        self._stop_on_divergence_types = set(stop_on_divergence_types if stop_on_divergence_types is not None else {"tool"})
        self._recorded = [_RecordedStep(step, i) for i, step in enumerate(trace.steps)]
        self._fidelity = ReplayFidelity(UnusedRecording(r.data.name, r.index) for r in self._recorded)

    def _find_recording(
        self, name: str, input: Any, rule: Optional[ReplayStepRule] = None,
    ) -> tuple[Optional[_RecordedStep], bool]:
        """Picks the recorded step for a call: among unused recordings with the same name, the one whose input is
        identical (key order and the rule's ignored fields aside), otherwise the earliest — which is reported as
        diverged. A name-only rule matches the earliest."""
        candidates = [r for r in self._recorded or [] if not r.consumed and r.data.name == name]
        if not candidates:
            return None, False
        if rule is not None and rule.match_on_name_only:
            return candidates[0], True

        ignore = tuple(sorted(rule.ignore_input_fields)) if rule is not None else ()
        if not ignore:
            actual = _input_key(input)
            match = next((r for r in candidates if r.canonical_input == actual), None)
        else:
            actual = _ignoring_key(input, ignore)
            match = next((r for r in candidates if r.key_ignoring(ignore) == actual), None)
        return match or candidates[0], match is not None

    def _stops_on_divergence(self, rule: Optional[ReplayStepRule], recorded_type: Optional[str]) -> bool:
        """Whether a diverged call of this step stops a live replay: the step's rule decides, else its recorded type."""
        if rule is not None and rule.on_divergence is not None:
            return rule.on_divergence == STOP
        return recorded_type is not None and recorded_type in self._stop_on_divergence_types

    def _stop_at(
        self, name: str, reason: str, recorded_input: Any, input: Any, recorded_type: Optional[str],
    ) -> ReplayStoppedError:
        """Stops a live replay at this call: records the stop and a failed step for it, and returns the error that
        ends the run."""
        position = len(self._steps) + 1
        if reason == STOP_DIVERGED:
            detail = (
                f"its input matches no unused recording of '{name}', so what the real step would return is unknown. "
                f"Recorded input: {_one_line(recorded_input)}. Actual input: {_one_line(input)}."
            )
            advice = (
                " If the model took a different path, record that path. If your code changed how the step is called, "
                "re-record, or replay mocked to test this change. If this input doesn't affect the step's output, "
                "match it on name only (step_rules)."
            )
        else:
            detail = f"the recording has no unused call of '{name}' to serve ({self._recorded_calls_of(name)})."
            advice = (
                " If the model took a different path, record that path. If your code added this call, re-record, or "
                "replay mocked to test this change."
            )
        message = (
            f"Live replay stopped at step {position} ('{name}'): {detail} "
            "The replay is inconclusive: it neither passed nor failed." + advice
        )

        stop = ReplayStop(position, name, reason, recorded_input, input, message)
        assert self._fidelity is not None
        self._fidelity._stopped(stop)

        s = Step(name)
        s.input = input
        s.type = recorded_type or "custom"
        s.metadata["_source"] = "replay"
        s.fail(message)
        self._steps.append(s)
        return ReplayStoppedError(stop)

    def _recorded_calls_of(self, name: str) -> str:
        count = sum(1 for r in self._recorded or [] if r.data.name == name)
        return "the trace never recorded it" if count == 0 else f"its {count} recorded call(s) were already used"

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
        self._keys_ignoring: dict[tuple[str, ...], str] = {}

    def key_ignoring(self, ignore: tuple[str, ...]) -> str:
        """The input's matching key with these (sorted) fields dropped, computed once per field set."""
        if ignore not in self._keys_ignoring:
            self._keys_ignoring[ignore] = _ignoring_key(self.data.input, ignore)
        return self._keys_ignoring[ignore]
