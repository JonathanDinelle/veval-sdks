from __future__ import annotations
from dataclasses import dataclass, field
from typing import Any, Iterable, Optional

# How a replayed call was bound to the recording:
#   "matched"  — served the recorded step with the same name and an identical input.
#   "diverged" — no recorded step with that name had an identical input; served the next unused one in recorded order.
#   "live"     — a model step that ran for real in a live replay (live_model_steps).
MATCHED = "matched"
DIVERGED = "diverged"
LIVE = "live"


@dataclass(frozen=True)
class ReplayBinding:
    """One replayed call and the recorded step bound to it. recorded_index is None for a live model call
    beyond the ones recorded."""
    step_name: str
    kind: str
    recorded_index: Optional[int]
    recorded_input: Any
    actual_input: Any


# What a live replay does with a call whose input matches no unused recording of its step:
#   "stop"  — stop the run at that step. The replay ends inconclusive: neither passed nor failed.
#   "serve" — serve the next unused recording of that step and report the call as diverged in the fidelity report.
STOP = "stop"
SERVE = "serve"

# Why a live replay stopped:
#   "diverged"     — the call's input matched no unused recording of its step, and the step stops on divergence.
#   "no_recording" — the recording has no unused call of that step to serve, and it isn't a model step that can run live.
STOP_DIVERGED = "diverged"
STOP_NO_RECORDING = "no_recording"


@dataclass
class ReplayStepRule:
    """How replay matches calls of one step to its recordings, and what a live replay does when none matches."""
    # Match calls on the step name alone, so a changed input never counts as divergence. For steps whose input
    # doesn't affect what they return, such as narration whose wording follows the model.
    match_on_name_only: bool = False
    # Input property names dropped at any depth before matching (e.g. "reasoning", "request_id").
    ignore_input_fields: set[str] = field(default_factory=set)
    # Live replay: what a diverged call does, "stop" or "serve". None uses ReplayOptions.stop_on_divergence_types:
    # stop when the step was recorded with one of those types, serve otherwise.
    on_divergence: Optional[str] = None


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


@dataclass(frozen=True)
class UnusedRecording:
    """A recorded step the replay never called."""
    step_name: str
    recorded_index: int


class ReplayFidelity:
    """How faithfully a mocked replay followed its recording. A replay is faithful when every call matched a
    recorded step with an identical input and every recorded step was called."""

    def __init__(self, recorded: Iterable[UnusedRecording]):
        self._bindings: list[ReplayBinding] = []
        self._unused: list[UnusedRecording] = list(recorded)
        self._stop: Optional[ReplayStop] = None

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
        return self._no_live_step_failure(trace_id) if live_model_steps else self._unused_failure(trace_id)

    def _unused_failure(self, trace_id: str) -> Optional[str]:
        """The failure for recorded steps that were never called, or None when every recording was used."""
        if not self._unused:
            return None
        steps = ", ".join(f"'{u.step_name}' (recorded step {u.recorded_index + 1})" for u in self._unused)
        return (
            f"Replay never called {len(self._unused)} recorded step(s) from trace {trace_id}: {steps}. "
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
