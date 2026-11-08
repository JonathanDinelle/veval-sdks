from __future__ import annotations
import math
from typing import Optional, TYPE_CHECKING

if TYPE_CHECKING:
    from ._fidelity import ReplayFidelity, ReplayStop
    from ._scenarios import ScenarioRunResult


def replay_summary(failures: list[str], stop: Optional[ReplayStop], fidelity: Optional[ReplayFidelity]) -> str:
    """The one-line summary on a replay or item result, e.g.
    "passed — 40/64 recorded steps used, 3 diverged, 12 live, stayed on path"."""
    if stop is not None:
        head = f"inconclusive at step {stop.step} '{stop.step_name}'"
    else:
        head = "passed" if not failures else f"failed ({_count(len(failures), 'failure')})"
    if fidelity is None:
        return head

    used = len({b.recorded_index for b in fidelity.bindings if b.recorded_index is not None})
    recorded = used + len(fidelity.unused)
    return (
        f"{head} — {used}/{recorded} recorded steps used, {fidelity.diverged} diverged, {fidelity.live} live, "
        + ("left the path" if stop is not None else "stayed on path")
    )


def scenario_summary(result: ScenarioRunResult) -> str:
    """The one-line summary on a scenario run result."""
    line = (
        f"{'passed' if result.passed else 'failed'} — {result.pass_count} passed, {result.fail_count} failed, "
        f"{result.inconclusive_count} inconclusive"
    )
    if result.inconclusive_count > 0:
        rate, allowed = _percent(result.inconclusive_rate), _percent(result.max_inconclusive_rate)
        line += (
            f" ({rate} > {allowed} allowed)" if result.inconclusive_rate > result.max_inconclusive_rate
            else f" ({rate}, within {allowed} allowed)"
        )
    replayed = [r for r in result.results if r.fidelity is not None]
    if replayed:
        stayed = sum(1 for r in replayed if r.inconclusive is None)
        line += f", {stayed}/{len(replayed)} replays stayed on path"
    return line


def _count(n: int, noun: str) -> str:
    return f"1 {noun}" if n == 1 else f"{n} {noun}s"


def _percent(rate: float) -> str:
    return f"{math.floor(rate * 100 + 0.5)}%"
