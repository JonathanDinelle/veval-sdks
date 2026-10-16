from __future__ import annotations
from dataclasses import dataclass
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
