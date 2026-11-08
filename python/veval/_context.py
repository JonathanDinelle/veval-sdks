from __future__ import annotations
import inspect
import json
from typing import Any, Callable, Awaitable, Optional, TypeVar, TYPE_CHECKING
from ._step import Step, StepHandle, StepOptions
from ._faults import EMPTY, MALFORMED, ReplayFault
from ._fidelity import (
    DIVERGED, FAULTED, LIVE, MATCHED, STOP, STOP_DIVERGED, STOP_NO_RECORDING,
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
        self._call_counts: dict[str, int] = {}

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
        options: Optional[StepOptions] = None,
    ) -> T:
        """options: settings known before the step runs, such as its type (StepOptions.type)."""
        served, output, step_type = self._serve_from_recording(name, input, options)
        if served:
            return output

        s, handle = self._start_step(name, input, step_type)
        try:
            result = await (step(handle) if _takes_handle(step) else step())
            s.complete(result)
            return result
        except Exception as ex:
            s.fail(str(ex))
            raise

    def track_step(
        self, name: str, input: Any, step: Callable[..., T], options: Optional[StepOptions] = None,
    ) -> T:
        """Synchronous track_step_async, for a step whose work is a plain function. Replays the same way."""
        served, output, step_type = self._serve_from_recording(name, input, options)
        if served:
            return output

        s, handle = self._start_step(name, input, step_type)
        try:
            result = _not_awaitable(step(handle) if _takes_handle(step) else step(), "track_step_async")
            s.complete(result)
            return result
        except Exception as ex:
            s.fail(str(ex))
            raise

    def _start_step(self, name: str, input: Any, step_type: Optional[str]) -> tuple[Step, StepHandle]:
        s = Step(name)
        s.input = input
        s.type = step_type or "custom"
        self._steps.append(s)
        return s, StepHandle(s)

    def _serve_from_recording(
        self, name: str, input: Any, options: Optional[StepOptions],
    ) -> tuple[bool, Any, Optional[str]]:
        """In a replay, binds the call to a recording. Returns (True, recorded output, type) when the step is served
        from the recording, or (False, None, type) when it should run. type is the step's type: the recorded one,
        else the call site's."""
        if options is not None and not isinstance(options, StepOptions):
            raise TypeError(f"track_step options must be a StepOptions, not {type(options).__name__}")
        declared_type = options.type if options is not None else None
        if self._recorded is None:
            return False, None, declared_type
        assert self._fidelity is not None
        # Once a live replay has stopped, nothing after it can be served truthfully, even if the agent caught the stop.
        if self._fidelity.stop is not None:
            raise ReplayStoppedError(self._fidelity.stop)

        rule = self._step_rules.get(name)
        if rule is not None and not isinstance(rule, ReplayStepRule):
            raise TypeError(f"step_rules['{name}'] must be a ReplayStepRule, not {type(rule).__name__}")
        call = self._call_counts.get(name, 0) + 1
        self._call_counts[name] = call
        fault_index = self._fault_for(name, input, call, rule)
        if fault_index is not None:
            return True, self._serve_fault(name, input, fault_index, rule, declared_type), None

        recorded, exact = self._find_recording(name, input, rule)
        recorded_type = recorded.data.type if recorded is not None else self._recorded_type_of(name)
        # A recorded llm or tool type wins; a recorded custom type is what an untyped step is saved as, so it says
        # nothing and the call site's type applies. A mislabelled call still can't turn a recorded tool step live.
        known_type = recorded_type if recorded_type and recorded_type != "custom" else None
        effective_type = known_type or declared_type or recorded_type
        # In live mode a model step runs for real, including extra calls of a step recorded as a model step and
        # model steps the recording never contained.
        run_live = self._live_model_steps and effective_type == "llm"
        conflicting_type = (
            declared_type if known_type and declared_type and declared_type != known_type else None
        )

        # After a fault, a mocked replay serves only calls that still match their recording: a fallback the
        # recording never called, or a model call whose prompt now carries the error, has no truthful output.
        if not self._live_model_steps and self._fidelity._first_fired is not None and (recorded is None or not exact):
            raise self._stop_at(
                name, STOP_NO_RECORDING if recorded is None else STOP_DIVERGED,
                recorded.data.input if recorded is not None else None, input, effective_type)

        if recorded is None and not run_live and self._strict_mock_mode:
            if self._live_model_steps:
                raise self._stop_at(name, STOP_NO_RECORDING, None, input, effective_type)
            available = ", ".join(dict.fromkeys(r.data.name for r in self._recorded if not r.consumed))
            raise RuntimeError(
                f"Replay mode: no mock output for step '{name}'. "
                f"Available steps: {available}. "
                "This would have made a real LLM call."
            )

        # A live model handed a recording from another path would reason over a page it never reached.
        if (self._live_model_steps and not run_live and recorded is not None and not exact
                and self._stops_on_divergence(rule, effective_type)):
            raise self._stop_at(name, STOP_DIVERGED, recorded.data.input, input, effective_type)

        if recorded is not None:
            recorded.consumed = True

        if run_live:
            self._fidelity._record(ReplayBinding(
                name, LIVE, recorded.index if recorded else None, recorded.data.input if recorded else None, input,
                conflicting_type,
            ))
            return False, None, effective_type
        if recorded is None:
            return False, None, effective_type

        self._fidelity._record(ReplayBinding(
            name, MATCHED if exact else DIVERGED, recorded.index, recorded.data.input, input, conflicting_type,
        ))
        mock = recorded.data.output
        s = Step(name)
        s.input = input
        # The recorded type, or the call site's when the recording saved the step untyped, so tool_called and
        # snapshot types match the original run wherever the recording knew the step's type.
        s.type = effective_type or "custom"
        s.metadata["_source"] = "replay"
        s.complete(mock)
        self._steps.append(s)
        return True, mock, effective_type

    def set_metadata(self, key: str, value: Any) -> None:
        self._metadata[key] = value

    def load_mock_outputs(
        self,
        trace: TraceData,
        strict: bool = True,
        live_model_steps: bool = False,
        step_rules: Optional[dict[str, ReplayStepRule]] = None,
        stop_on_divergence_types: Optional[set[str]] = None,
        faults: Optional[list[ReplayFault]] = None,
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
        self._fidelity = ReplayFidelity((UnusedRecording(r.data.name, r.index) for r in self._recorded), faults or ())

    def _fault_for(self, name: str, input: Any, call: int, rule: Optional[ReplayStepRule]) -> Optional[int]:
        """The index of the first declared fault that applies to this call of the step, or None."""
        assert self._fidelity is not None
        for i, fault in enumerate(self._fidelity._faults):
            if fault.step_name != name or call < fault.from_call:
                continue
            if fault.count is not None and self._fidelity._fired[i] >= fault.count:
                continue
            if fault.has_input:
                ignore = tuple(sorted(rule.ignore_input_fields)) if rule is not None else ()
                if _ignoring_key(fault.input, ignore) != _ignoring_key(input, ignore):
                    continue
            return i
        return None

    def _serve_fault(
        self, name: str, input: Any, fault_index: int, rule: Optional[ReplayStepRule], declared_type: Optional[str],
    ) -> Any:
        """Serves a fault at this call: records it as a step with _source = "fault" and either raises the fault's
        exception or returns its malformed output. The call uses up no recording, so a retry is served the recorded one."""
        assert self._fidelity is not None
        fault = self._fidelity._faults[fault_index]
        self._fidelity._fire(fault_index)
        self._fidelity._record(ReplayBinding(name, FAULTED, None, None, input))

        recorded_type = self._recorded_type_of(name)
        step_type = (declared_type or recorded_type) if not recorded_type or recorded_type == "custom" else recorded_type
        s = Step(name)
        s.input = input
        s.type = step_type or "custom"
        s.metadata["_source"] = "fault"
        s.metadata["_fault"] = fault.description
        self._steps.append(s)

        if fault.kind != MALFORMED:
            exception = fault._create_exception()
            s.fail(str(exception))
            raise exception

        try:
            value = self._malformed_output_of(fault, name, input, rule)
        except Exception as ex:
            s.fail(str(ex))
            raise
        s.complete(value)
        return value

    def _malformed_output_of(self, fault: ReplayFault, name: str, input: Any, rule: Optional[ReplayStepRule]) -> Any:
        if fault.malformed_output is None:
            return fault.malformed_value
        recorded, _ = self._find_recording(name, input, rule)
        output = recorded.data.output if recorded is not None else None
        if fault.malformed_output == EMPTY:
            return "" if isinstance(output, str) else None
        if recorded is None:
            raise RuntimeError(
                f"Fault on '{name}': there is no unused recording of the step to truncate. "
                "Give the malformed value yourself instead.")
        text = output if isinstance(output, str) else json.dumps(output, default=str, ensure_ascii=False, separators=(",", ":"))
        return text[: len(text) // 2]

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
        """Whether a diverged call of this step stops a live replay: the step's rule decides, else its type."""
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
        assert self._fidelity is not None
        fault = self._fidelity._first_fired
        if fault is not None and not self._live_model_steps:
            advice = (
                " Past the fault the agent took a path the recording doesn't cover (a fallback it never called, or a "
                "call whose input now carries the error), so no recorded output can stand in for it. Replay live to run "
                "model steps for real, or record a trace of this path."
            )
        where = f" after an injected fault ({fault.description})" if fault is not None else ""
        message = (
            f"{'Live replay' if self._live_model_steps else 'Replay'} stopped at step {position} ('{name}'){where}: "
            f"{detail} The replay is inconclusive: it neither passed nor failed." + advice
        )

        stop = ReplayStop(position, name, reason, recorded_input, input, message)
        assert self._fidelity is not None
        self._fidelity._stopped(stop)

        s = Step(name)
        s.input = input
        s.type = recorded_type or "custom"
        s.metadata["_source"] = "replay"
        s._stopped(message)
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
