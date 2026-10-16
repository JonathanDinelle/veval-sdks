from __future__ import annotations
from dataclasses import dataclass
from typing import Any, Iterable, Optional

# How a replayed call was bound to the recording:
#   "matched"  — served the recorded step with the same name and an identical input.
#   "diverged" — no recorded step with that name had an identical input; served the next unused one in recorded order.
MATCHED = "matched"
DIVERGED = "diverged"


@dataclass(frozen=True)
class ReplayBinding:
    """One replayed call and the recorded step that served it."""
    step_name: str
    kind: str
    recorded_index: int
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
    def is_faithful(self) -> bool:
        return self.diverged == 0 and not self._unused

    def _record(self, binding: ReplayBinding) -> None:
        self._bindings.append(binding)
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
