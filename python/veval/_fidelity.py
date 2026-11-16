from __future__ import annotations
from dataclasses import dataclass, field
from typing import Any, Callable, Iterable, Optional, Sequence
from ._faults import FaultReport, ReplayFault

# How a replayed call was bound to the recording:
#   "matched"  — served the recorded step with the same name and an identical input.
#   "diverged" — no recorded step with that name had an identical input; served the next unused one in recorded order.
#   "live"     — a model step that ran for real in a live replay (live_model_steps).
#   "faulted"  — served an injected fault (ReplayOptions.faults) instead of running or using a recording.
#   "not_replayed" — tool-live replay: a tool marked reads_external_state, served from the recording instead of running.
# "live" is any step that ran for real: a model step in a live replay, a tool in a tool-live replay.
MATCHED = "matched"
DIVERGED = "diverged"
LIVE = "live"
FAULTED = "faulted"
NOT_REPLAYED = "not_replayed"


@dataclass(frozen=True)
class ReplayBinding:
    """One replayed call and the recorded step bound to it. recorded_index is None for a live model call
    beyond the ones recorded."""
    step_name: str
    kind: str
    recorded_index: Optional[int]
    recorded_input: Any
    actual_input: Any
    # The type the call site gave (StepOptions.type) when it differs from the recorded type, or None. The recorded
    # type won: a mislabelled call can't turn a recorded tool step live.
    declared_type: Optional[str] = None


# What a live replay does with a call whose input matches no unused recording of its step:
#   "stop"  — stop the run at that step. The replay ends inconclusive: neither passed nor failed.
#   "serve" — serve the next unused recording of that step and report the call as diverged in the fidelity report.
STOP = "stop"
SERVE = "serve"

# Why a replay stopped:
#   "diverged"     — the call's input matched no unused recording of its step, and the step stops on divergence.
#   "no_recording" — the recording has no unused call of that step to serve, and it isn't a model step that can run live.
#   "after_fault"  — an injected fault (ReplayOptions.faults) sent the agent off its recording: this call matches no
#                    unused recording. Past the fault the recording can't say what the real step would return.
STOP_DIVERGED = "diverged"
STOP_NO_RECORDING = "no_recording"
STOP_AFTER_FAULT = "after_fault"


@dataclass
class ReplayStepRule:
    """How replay matches calls of one step to its recordings, and what a live replay does when none matches."""
    # Match calls on the step name alone, so a changed input never counts as divergence. For steps whose input
    # doesn't affect what they return, such as narration whose wording follows the model.
    match_on_name_only: bool = False
    # Input property names dropped at any depth before matching (e.g. "reasoning", "request_id").
    ignore_input_fields: set[str] = field(default_factory=set)
    # Tool-live replay (live_tool_steps): output property names dropped at any depth before a tool's new output is
    # compared with its recorded output, for values that change on every call (ids, timestamps).
    ignore_output_fields: set[str] = field(default_factory=set)
    # Tool-live replay: the tool reads state the recording doesn't hold (a live web page, a database, the clock), so
    # running it again can't be compared with the recording. It is served from the recording and reported as not
    # replayed, never as passed. Test such tools with real runs and assertions.
    reads_external_state: bool = False
    # Live replay: what a diverged call does, "stop" or "serve". None uses ReplayOptions.stop_on_divergence_types:
    # stop when the step was recorded with one of those types, serve otherwise.
    on_divergence: Optional[str] = None

    def __post_init__(self) -> None:
        if self.on_divergence not in (None, STOP, SERVE):
            raise ValueError(f"ReplayStepRule.on_divergence must be 'stop', 'serve' or None, not {self.on_divergence!r}")
        if isinstance(self.ignore_input_fields, str):
            raise TypeError(
                "ReplayStepRule.ignore_input_fields takes a collection of field names, such as {\"reasoning\"}, "
                f"not the string {self.ignore_input_fields!r}"
            )
        self.ignore_input_fields = set(self.ignore_input_fields)


@dataclass(frozen=True)
class ReplayStop:
    """Where a live replay left its recording and stopped. Past this step nobody knows what the real tools would have
    returned, so the replay is inconclusive: its assertions are not evaluated and it neither passes nor fails.
    step is the 1-based position of the call among the replay's steps; recorded_input is None when there was no
    recording to compare with."""
    step: int
    step_name: str
    reason: str
    recorded_input: Any
    actual_input: Any
    message: str


class ReplayStoppedError(Exception):
    """Raised from a step when a live replay stops there (ReplayStop). Let it propagate: if the agent catches it, the
    replay is still inconclusive, and every later step raises it again."""

    def __init__(self, stop: ReplayStop):
        super().__init__(stop.message)
        self.stop = stop


class RecordedStepError(Exception):
    """Raised from a step when replay serves a recording of that step that failed in the recorded run, so the agent's
    retry or fallback runs as it did then. It carries the recorded error message, not the original exception's class:
    code that only catches a specific exception type won't catch it."""

    def __init__(self, step_name: str, recorded_error: Optional[str]):
        super().__init__(
            f"Step '{step_name}' failed in the recorded run: {recorded_error or 'no error message recorded'}")
        self.step_name = step_name
        # The error the recorded step failed with, or None when the recording didn't keep one.
        self.recorded_error = recorded_error


@dataclass(frozen=True)
class UnusedRecording:
    """A recorded step the replay never called."""
    step_name: str
    recorded_index: int


@dataclass(frozen=True)
class ToolOutputCheck:
    """Tool-live replay: one tool that ran for real, with its recorded and new outputs. changed when they differ (key
    order and the step rule's ignore_output_fields aside). actual_output is the error message when the tool raised."""
    step_name: str
    # The 0-based position of the recording the call was compared with.
    recorded_index: int
    recorded_output: Any
    actual_output: Any
    changed: bool
    # The failure for a changed output; None when the output matched.
    message: Optional[str] = None


class ReplayFidelity:
    """How faithfully a mocked replay followed its recording. A replay is faithful when every call matched a
    recorded step with an identical input and every recorded step was called."""

    def __init__(
        self, recorded: Iterable[UnusedRecording], faults: Sequence[ReplayFault] = (), live_tool_steps: bool = False,
    ):
        # The replay runs tools for real (ReplayOptions.live_tool_steps).
        self.live_tool_steps = live_tool_steps
        self._tool_checks: list[ToolOutputCheck] = []
        self._bindings: list[ReplayBinding] = []
        self._unused: list[UnusedRecording] = list(recorded)
        self._stop: Optional[ReplayStop] = None
        self._faults: list[ReplayFault] = list(faults)
        self._fired: list[int] = [0] * len(self._faults)
        # The first fault that fired, or None while none has.
        self._first_fired: Optional[ReplayFault] = None
        # Recordings from this index on may go unused: a fault that fails every call fired there. None while none has.
        self._unused_excused_from: Optional[int] = None
        self._never_excused: set[int] = set()

    @property
    def faulted(self) -> int:
        """Calls served an injected fault."""
        return sum(1 for b in self._bindings if b.kind == FAULTED)

    @property
    def faults(self) -> list[FaultReport]:
        """Each fault in ReplayOptions.faults and how many times it fired, in declared order."""
        return [FaultReport(f, n) for f, n in zip(self._faults, self._fired)]

    def _fire(self, fault_index: int, first_matching_recording: Optional[int], untargeted: Iterable[int]) -> None:
        """first_matching_recording: the first unused recording the fault would have hit, or None; for a fault that
        fails every call, recordings from there on are excused. untargeted: recordings of the faulted step the fault
        would never have hit (other inputs, earlier calls); they are never excused, since the fault gives the agent no
        reason to skip them."""
        self._fired[fault_index] += 1
        fault = self._faults[fault_index]
        if self._first_fired is None:
            self._first_fired = fault
        if fault.count is not None or self._fired[fault_index] > 1:
            return
        self._never_excused.update(untargeted)
        # A fault that fails every call sends the agent down its fallback path from here: recordings of the faulted
        # step and everything after it may rightly go unused. Recordings before it were the agent's to call.
        used = [b.recorded_index + 1 for b in self._bindings if b.recorded_index is not None]
        start = first_matching_recording if first_matching_recording is not None else max(used, default=0)
        self._unused_excused_from = start if self._unused_excused_from is None else min(self._unused_excused_from, start)

    @property
    def stop(self) -> Optional[ReplayStop]:
        """Where a live replay left its recording and stopped, or None when it didn't."""
        return self._stop

    def _stopped(self, stop: ReplayStop) -> None:
        if self._stop is None:
            self._stop = stop

    @property
    def bindings(self) -> list[ReplayBinding]:
        """Each replayed call, in call order."""
        return list(self._bindings)

    @property
    def unused(self) -> list[UnusedRecording]:
        """Recorded steps no call consumed, in recorded order."""
        return list(self._unused)

    @property
    def matched(self) -> int:
        return sum(1 for b in self._bindings if b.kind == MATCHED)

    @property
    def diverged(self) -> int:
        return sum(1 for b in self._bindings if b.kind == DIVERGED)

    @property
    def live(self) -> int:
        """Model steps that ran for real in a live replay."""
        return sum(1 for b in self._bindings if b.kind == LIVE)

    @property
    def is_faithful(self) -> bool:
        """No call diverged and every recorded step was called. Live calls don't count against it."""
        return self.diverged == 0 and not self._unused

    def _record(self, binding: ReplayBinding) -> None:
        self._bindings.append(binding)
        if binding.recorded_index is not None:
            self._unused = [u for u in self._unused if u.recorded_index != binding.recorded_index]

    def _failure(self, trace_id: str, live_model_steps: bool) -> Optional[str]:
        """The replay's binding failure, or None. A mocked replay fails when a recorded step was never called; a live
        replay fails when no step ran live (a live model may legitimately skip recorded steps). Not called when the
        agent raised, since the exception already explains the rest."""
        if live_model_steps:
            return self._no_live_step_failure(trace_id)
        if self.live_tool_steps:
            return self._no_tool_ran_failure(trace_id) or self._unused_failure(trace_id)
        return self._unused_failure(trace_id)

    @property
    def tool_checks(self) -> list[ToolOutputCheck]:
        """Tool-live replay: each tool that ran for real and how its output compared, in call order."""
        return list(self._tool_checks)

    @property
    def tool_output_changes(self) -> list[ToolOutputCheck]:
        """Tool-live replay: the tools whose new output differs from the recording."""
        return [c for c in self._tool_checks if c.changed]

    @property
    def not_replayed(self) -> int:
        """Tool-live replay: tools served from the recording because they read external state."""
        return sum(1 for b in self._bindings if b.kind == NOT_REPLAYED)

    def _checked(self, check: ToolOutputCheck) -> None:
        self._tool_checks.append(check)

    def _no_tool_ran_failure(self, trace_id: str) -> Optional[str]:
        """The failure for a tool-live replay in which no tool ran, or None when at least one did."""
        if self._tool_checks:
            return None
        return (
            f"Tool-live replay of trace {trace_id} ran no tool: none of the steps it called was recorded as type 'tool' "
            + (f"except {self.not_replayed} that read external state, " if self.not_replayed else "")
            + 'so no tool output was checked. Mark tool steps with StepOptions(type=StepType.TOOL) (or set_meta("type", "tool")) '
            "and record a new trace."
        )

    def _unfired_fault_failures(self, calls_of: Callable[[str], int]) -> list[str]:
        """One failure per declared fault that never fired: a fault test whose fault didn't fire tested nothing."""
        failures = []
        for fault, fired in zip(self._faults, self._fired):
            if fired:
                continue
            calls = calls_of(fault.step_name)
            why = (f"the agent never called '{fault.step_name}'" if calls == 0
                   else f"none of the {calls} call(s) of '{fault.step_name}' matched it")
            failures.append(
                f"Fault not triggered: {fault.description} never fired: {why}. "
                "The failure path it was meant to test did not run.")
        return failures

    def _unused_failure(self, trace_id: str) -> Optional[str]:
        """The failure for recorded steps that were never called, or None when every recording was used. After a fault
        that fails every call, recordings from the faulted step on are excused: the agent took its fallback path.
        Recordings of that step the fault doesn't target (another input, an earlier call) are not. A fault limited with
        times(n) is expected to rejoin the recording."""
        start = self._unused_excused_from
        unused = [u for u in self._unused
                  if start is None or u.recorded_index < start or u.recorded_index in self._never_excused]
        if not unused:
            return None
        steps = ", ".join(f"'{u.step_name}' (recorded step {u.recorded_index + 1})" for u in unused)
        return (
            f"Replay never called {len(unused)} recorded step(s) from trace {trace_id}: {steps}. "
            "The agent skipped part of the recorded run, so its assertions may be passing on a path the recording does not cover."
        )

    def _no_live_step_failure(self, trace_id: str) -> Optional[str]:
        """The failure for a live replay in which nothing ran live, or None when at least one model step did."""
        if self.live > 0:
            return None
        return (
            f"Live replay of trace {trace_id} ran no step live: none of the steps it called was recorded as type 'llm', so every "
            "output came from the recording and a prompt change could not show up. Mark model steps with "
            'set_meta("type", "llm") and record a new trace, or replay in mocked mode.'
        )
