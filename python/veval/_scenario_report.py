from __future__ import annotations
from typing import Any

from ._http_client import VevalHttpClient, VevalSendError
from ._scenarios import ScenarioRunResult


class ScenarioReport:
    """Reports one scenario run. Item traces are sent without raising, so one failed send can't abort
    the remaining items; each failure is counted, and in strict mode complete_async raises once, after
    the run is posted."""

    def __init__(self, client: VevalHttpClient, scenario_name: str):
        self._client = client
        self._scenario_name = scenario_name
        self._undelivered = 0

    async def send_trace_async(self, payload: Any) -> None:
        if not await self._client.send_trace_async(payload, raise_on_failure=False):
            self._undelivered += 1

    async def complete_async(self, result: ScenarioRunResult, unnamed_item: str) -> None:
        """Posts the run for trend tracking. Raises only in strict mode (raise_on_send_failure).
        unnamed_item is the name posted for an item with neither a name nor a trace_id."""
        await self._client.post_scenario_run_async(self._scenario_name, {
            "passed": result.passed,
            "pass_count": result.pass_count,
            "fail_count": result.fail_count,
            "results": [
                {
                    "name": r.item.name or r.item.trace_id or unnamed_item,
                    "passed": r.passed,
                    "failures": r.failures,
                    "judgments": r.context.judgments if r.context else [],
                }
                for r in result.results
            ],
        })

        if self._undelivered and self._client.raise_on_send_failure:
            raise VevalSendError(
                f"Veval: {self._undelivered} trace(s) from scenario '{self._scenario_name}' could not be stored; "
                "see the warnings above"
            )
