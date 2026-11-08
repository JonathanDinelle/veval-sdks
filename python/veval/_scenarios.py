from __future__ import annotations
from dataclasses import dataclass, field
from typing import Any, Optional, TYPE_CHECKING
from ._assertions import ITraceAssertion
from ._fidelity import ReplayFidelity, ReplayStepRule, ReplayStop
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
    # The largest share of inconclusive items (0 to 1) a passing run may have. Defaults to 0: any inconclusive item
    # fails the run, so a change that sends every item off its recorded path can't look green. Raise it to accept
    # some items needing a real run, e.g. 0.1 for one in ten.
    max_inconclusive_rate: float = 0.0

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
    def outcome(self) -> str:
        """How the item ended: "passed", "failed", or "inconclusive" (its live replay left the recording and stopped)."""
        if self.inconclusive is not None:
            return "inconclusive"
        return "passed" if not self.failures else "failed"

    @property
    def summary(self) -> str:
        """One line, e.g. "passed — 40/64 recorded steps used, 3 diverged, 12 live, stayed on path"."""
        return replay_summary(self.failures, self.inconclusive, self.fidelity)


@dataclass
class ScenarioRunResult:
    results: list[ItemRunResult] = field(default_factory=list)
    # The ScenarioOptions.max_inconclusive_rate the run was judged against.
    max_inconclusive_rate: float = 0.0

    @property
    def passed(self) -> bool:
        """True when no item failed and the share of inconclusive items is within max_inconclusive_rate (by default,
        none may be inconclusive)."""
        return self.fail_count == 0 and self.inconclusive_rate <= self.max_inconclusive_rate

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
    def inconclusive_rate(self) -> float:
        """Inconclusive items as a share of all items, from 0 to 1."""
        return self.inconclusive_count / len(self.results) if self.results else 0.0

    @property
    def stayed_on_path_rate(self) -> Optional[float]:
        """Replayed items that stayed on their recorded path (didn't stop), as a share of replayed items; None when no
        item was replayed. How far live replay can be trusted for this change."""
        replayed = [r for r in self.results if r.fidelity is not None]
        if not replayed:
            return None
        return sum(1 for r in replayed if r.inconclusive is None) / len(replayed)

    @property
    def summary(self) -> str:
        """One line, e.g. "failed — 5 passed, 0 failed, 1 inconclusive (17% > 0% allowed), 5/6 replays stayed on path"."""
        return scenario_summary(self)
