from __future__ import annotations
from dataclasses import dataclass, field
from typing import Any, Optional, TYPE_CHECKING
from ._assertions import ITraceAssertion
from ._fidelity import ReplayStepRule, ReplayStop

if TYPE_CHECKING:
    from ._context import VevalExecutionContext


@dataclass
class ScenarioItem:
    name: Optional[str] = None
    trace_id: Optional[str] = None
    input: Any = None
    assertions: list[ITraceAssertion] = field(default_factory=list)
    # For a trace-backed item: serve model steps from the recording too (mocked replay) instead of running
    # them live. Cheaper and deterministic, so it suits code changes, but it can't catch a prompt regression.
    # VevalTestSdk always replays mocked; for items loaded from the dashboard, pass mock_model_steps=True to
    # run_scenario_async.
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

    @property
    def passed(self) -> bool:
        """True only when the item passed; False when it failed or was inconclusive."""
        return len(self.failures) == 0 and self.inconclusive is None


@dataclass
class ScenarioRunResult:
    results: list[ItemRunResult] = field(default_factory=list)

    @property
    def passed(self) -> bool:
        """True when every item passed: a failed or inconclusive item makes the run not pass."""
        return all(r.passed for r in self.results)

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
