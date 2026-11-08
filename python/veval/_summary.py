from __future__ import annotations
import math
from typing import Optional, TYPE_CHECKING

if TYPE_CHECKING:
    from ._fidelity import ReplayFidelity, ReplayStop
    from ._scenarios import ScenarioRunResult


def replay_summary(failures: list[str], stop: Optional[ReplayStop], fidelity: Optional[ReplayFidelity]) -> str:
    """The one-line summary on a replay or item result, e.g.
    "passed — 40/64 recorded steps used, 3 diverged but served, 12 live, stayed on path"."""
    if stop is not None:
        head = f"inconclusive at step {stop.step} '{stop.step_name}'"
    else:
        head = "passed" if not failures else f"failed ({_count(len(failures), 'failure')})"
    if fidelity is None:
        return head

    used = len({b.recorded_index for b in fidelity.bindings if b.recorded_index is not None})
    recorded = used + len(fidelity.unused)
    return (
        f"{head} — {used}/{recorded} recorded steps used, {fidelity.diverged} diverged but served, {fidelity.live} live, "
        + ("left the path" if stop is not None else "stayed on path")
    )


def scenario_summary(result: ScenarioRunResult) -> str:
    """The one-line summary on a scenario run result."""
    too_many_inconclusive = result.inconclusive_rate > result.max_inconclusive_rate
    # The reason comes first: "failed ... 0 failed" would otherwise read like a bug.
    if result.passed:
        head = "passed"
    elif result.fail_count == 0 and too_many_inconclusive:
        head = "failed: too many inconclusive"
    else:
        head = "failed"
    line = (
        f"{head} — {result.pass_count} passed, {result.fail_count} failed, {result.inconclusive_count} inconclusive"
    )
    if result.inconclusive_count > 0:
        rate, allowed = _percent(result.inconclusive_rate), _percent(result.max_inconclusive_rate)
        line += (
            f" ({rate} of replays > {allowed} allowed)" if too_many_inconclusive
            else f" ({rate} of replays, within {allowed} allowed)"
        )
    replayed = sum(1 for r in result.results if r.replayed)
    if replayed:
        line += f", {replayed - result.inconclusive_count}/{replayed} replays stayed on path"
    return line


def _count(n: int, noun: str) -> str:
    return f"1 {noun}" if n == 1 else f"{n} {noun}s"


def _percent(rate: float) -> str:
    return f"{math.floor(rate * 100 + 0.5)}%"
