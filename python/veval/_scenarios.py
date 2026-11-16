from __future__ import annotations
from dataclasses import dataclass, field
from typing import Any, Optional, TYPE_CHECKING
from ._assertions import ITraceAssertion
from ._faults import FaultReport, ReplayFault
from ._fidelity import STOP_AFTER_FAULT, ReplayFidelity, ReplayStepRule, ReplayStop
from ._summary import replay_summary, scenario_summary

if TYPE_CHECKING:
    from ._context import VevalExecutionContext


@dataclass
class ScenarioOptions:
    """Settings for every item of a scenario run (run_scenario_async)."""
    # Replay every trace-backed item fully mocked, as if each set ScenarioItem.mock_model_steps; this is the switch
    # for items loaded from the dashboard. VevalTestSdk always replays mocked.
    mock_model_steps: bool = False
    # ReplayOptions.step_rules for every trace-backed item; an item's own entry for the same step replaces it.
    step_rules: dict[str, ReplayStepRule] = field(default_factory=dict)
    # The largest share of replayed items (0 to 1) that may be inconclusive in a passing run. Defaults to 0: any inconclusive item
    # fails the run, so a change that sends every item off its recorded path can't look green. Raise it to accept
    # some items needing a real run, e.g. 0.1 for one in ten.
    max_inconclusive_rate: float = 0.0
    # ReplayOptions.faults for every trace-backed item, before the item's own faults. Each item's replay gets its own
    # count, so a fault that never fires in an item fails that item. An item that stops after a fault is inconclusive
    # and always fails the run: the max_inconclusive_rate allowance is for live replays that leave their recording,
    # not for fault tests.
    faults: list[ReplayFault] = field(default_factory=list)

    def __post_init__(self) -> None:
        _check_rate(self.max_inconclusive_rate)


def _check_rate(rate: float) -> float:
    if isinstance(rate, bool) or not isinstance(rate, (int, float)) or not 0 <= rate <= 1:
        raise ValueError(f"max_inconclusive_rate is a share from 0 to 1, got {rate!r}.")
    return float(rate)


@dataclass
class ScenarioItem:
    name: Optional[str] = None
    trace_id: Optional[str] = None
    input: Any = None
    assertions: list[ITraceAssertion] = field(default_factory=list)
    # For a trace-backed item: serve model steps from the recording too (mocked replay) instead of running
    # them live. Cheaper and deterministic, so it suits code changes, but it can't catch a prompt regression.
    # VevalTestSdk always replays mocked; for items loaded from the dashboard, pass
    # ScenarioOptions(mock_model_steps=True) to run_scenario_async.
    mock_model_steps: bool = False
    # For a trace-backed item: ReplayOptions.step_rules for its replay. Replaces the scenario-level rule of the same step.
    step_rules: dict[str, ReplayStepRule] = field(default_factory=dict)
    # For a trace-backed item: ReplayOptions.faults for its replay, added after the scenario's faults. A synthetic item
    # (no trace_id) can't take faults and fails.
    faults: list[ReplayFault] = field(default_factory=list)


@dataclass
class ItemRunResult:
    item: ScenarioItem = field(default_factory=ScenarioItem)
    failures: list[str] = field(default_factory=list)
    context: Optional[VevalExecutionContext] = None
    # Where the item's live replay left its recording and stopped, or None. When set, its assertions were not evaluated.
    inconclusive: Optional[ReplayStop] = None
    # How the item's replay was bound to its recording; None for a synthetic item or a missing trace.
    fidelity: Optional[ReplayFidelity] = None

    @property
    def passed(self) -> bool:
        """True only when the item passed; False when it failed or was inconclusive."""
        return len(self.failures) == 0 and self.inconclusive is None

    @property
    def faults(self) -> list[FaultReport]:
        """Each fault injected into the item's replay and how many times it fired; empty when none was declared."""
        return self.fidelity.faults if self.fidelity is not None else []

    @property
    def inconclusive_after_fault(self) -> bool:
        """The item stopped because an injected fault sent it off its recording (reason "after_fault")."""
        return self.inconclusive is not None and self.inconclusive.reason == STOP_AFTER_FAULT

    @property
    def replayed(self) -> bool:
        """The item was a replay of a recorded trace (only a replay can be inconclusive)."""
        return self.fidelity is not None or self.inconclusive is not None

    @property
    def outcome(self) -> str:
        """How the item ended: "passed", "failed", or "inconclusive" (its live replay left the recording and stopped)."""
        if self.inconclusive is not None:
            return "inconclusive"
        return "passed" if not self.failures else "failed"

    @property
    def summary(self) -> str:
        """One line, e.g. "passed — 40/64 recorded steps used, 3 diverged but served, 12 live, stayed on path"."""
        return replay_summary(self.failures, self.inconclusive, self.fidelity)


@dataclass
class ScenarioRunResult:
    results: list[ItemRunResult] = field(default_factory=list)
    # The ScenarioOptions.max_inconclusive_rate the run was judged against.
    max_inconclusive_rate: float = 0.0

    @property
    def passed(self) -> bool:
        """True when no item failed, no item stopped after an injected fault, and the share of other inconclusive items
        is within max_inconclusive_rate (by default, none may be inconclusive)."""
        return (self.fail_count == 0 and self.inconclusive_after_fault_count == 0
                and self.inconclusive_rate <= self.max_inconclusive_rate)

    @property
    def pass_count(self) -> int:
        return sum(1 for r in self.results if r.passed)

    @property
    def fail_count(self) -> int:
        """Items that failed. Inconclusive items are counted in inconclusive_count, not here."""
        return sum(1 for r in self.results if not r.passed and r.inconclusive is None)

    @property
    def inconclusive_count(self) -> int:
        """Items whose live replay left its recording and stopped, so they neither passed nor failed."""
        return sum(1 for r in self.results if r.inconclusive is not None)

    @property
    def inconclusive_after_fault_count(self) -> int:
        """Inconclusive items that stopped after an injected fault, included in inconclusive_count. They are left out of
        inconclusive_rate and always fail the run: the fault test couldn't be judged, which no allowance should hide."""
        return sum(1 for r in self.results if r.inconclusive_after_fault)

    def _replayed_on_live_terms(self) -> int:
        """Replayed items, leaving out those stopped by a fault: the rates measure how far live replay stays on path."""
        return sum(1 for r in self.results if r.replayed and not r.inconclusive_after_fault)

    @property
    def inconclusive_rate(self) -> float:
        """Inconclusive items as a share of replayed items, from 0 to 1, leaving out items that stopped after an
        injected fault. Only a replay can be inconclusive, so synthetic items don't dilute it."""
        replayed = self._replayed_on_live_terms()
        other = self.inconclusive_count - self.inconclusive_after_fault_count
        return other / replayed if replayed else 0.0

    @property
    def stayed_on_path_rate(self) -> Optional[float]:
        """Replayed items that stayed on their recorded path (didn't stop), as a share of replayed items; None when no
        item was replayed. How far live replay can be trusted for this change. Items that stopped after an injected
        fault are left out: the fault sent them off the path."""
        replayed = self._replayed_on_live_terms()
        if not replayed:
            return None
        return (replayed - (self.inconclusive_count - self.inconclusive_after_fault_count)) / replayed

    @property
    def summary(self) -> str:
        """One line, e.g. "failed: too many inconclusive — 5 passed, 0 failed, 1 inconclusive (17% of replays > 0% allowed), 5/6 replays stayed on path"."""
        return scenario_summary(self)
