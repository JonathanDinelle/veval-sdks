from __future__ import annotations
from dataclasses import dataclass, field
from typing import Any, Optional, TYPE_CHECKING
from ._assertions import ITraceAssertion

if TYPE_CHECKING:
    from ._context import VevalExecutionContext


@dataclass
class ScenarioItem:
    name: Optional[str] = None
    trace_id: Optional[str] = None
    input: Any = None
    assertions: list[ITraceAssertion] = field(default_factory=list)


@dataclass
class ItemRunResult:
    item: ScenarioItem = field(default_factory=ScenarioItem)
    failures: list[str] = field(default_factory=list)
    context: Optional[VevalExecutionContext] = None

    @property
    def passed(self) -> bool:
        return len(self.failures) == 0


@dataclass
class ScenarioRunResult:
    results: list[ItemRunResult] = field(default_factory=list)

    @property
    def passed(self) -> bool:
        return all(r.passed for r in self.results)

    @property
    def pass_count(self) -> int:
        return sum(1 for r in self.results if r.passed)

    @property
    def fail_count(self) -> int:
        return sum(1 for r in self.results if not r.passed)
