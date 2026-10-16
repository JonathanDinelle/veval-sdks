from __future__ import annotations
import json
import logging
from typing import Any, Optional
from urllib.parse import quote
from urllib.request import Request, urlopen
from urllib.error import HTTPError
from ._tracing import TraceData
from ._snapshots import SnapshotData

_logger = logging.getLogger("veval")


class VevalSendError(Exception):
    """A trace or scenario run could not be delivered to Veval. Raised only when
    VevalOptions.raise_on_send_failure is set; otherwise the failure is logged as a warning."""

    def __init__(self, message: str, status: Optional[int] = None):
        super().__init__(message)
        self.status = status


class VevalRequestError(Exception):
    """Loading something the SDK needs from Veval failed, such as a scenario's items or a trace to
    replay. Raised whatever raise_on_send_failure says, since the caller can't continue without it."""

    def __init__(self, message: str, status: Optional[int] = None):
        super().__init__(message)
        self.status = status


class VevalHttpClient:
    def __init__(self, api_key: str, endpoint: str, raise_on_send_failure: bool = False):
        self._api_key = api_key
        self._endpoint = endpoint.rstrip("/")
        self._raise_on_send_failure = raise_on_send_failure

    @property
    def raise_on_send_failure(self) -> bool:
        return self._raise_on_send_failure

    def _headers(self) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {self._api_key}",
            "Content-Type": "application/json",
        }

    async def send_trace_async(self, payload: Any, raise_on_failure: Optional[bool] = None) -> bool:
        """Never raises unless raise_on_failure (default: the raise_on_send_failure option) is set;
        a failed send is logged as a warning instead, so tracing can't break the host app.
        Returns whether the trace was stored."""
        return await self._send_async("Sending trace", f"{self._endpoint}/v1/traces", payload, raise_on_failure)

    async def get_trace_async(self, trace_id: str) -> Optional[TraceData]:
        """Returns None only when the trace doesn't exist (404); any other failure raises
        VevalRequestError, so a wrong API key or a network error can't look like a missing trace."""
        data = await self._load_async(
            f"Loading trace '{trace_id}'", f"{self._endpoint}/v1/traces/{trace_id}", none_on_404=True
        )
        return TraceData.from_dict(data) if data is not None else None

    async def get_scenario_items_async(self, scenario_name: str) -> list[dict]:
        """Raises VevalRequestError on failure — returning an empty list would run zero items and
        report the scenario as passed."""
        encoded = quote(scenario_name, safe="")
        data = await self._load_async(
            f"Loading items for scenario '{scenario_name}'", f"{self._endpoint}/v1/scenarios/{encoded}/items"
        )
        return data.get("items") or []

    async def _load_async(self, what: str, url: str, none_on_404: bool = False) -> Optional[dict]:
        import asyncio
        get = self._get_or_none_on_404 if none_on_404 else self._get_json
        try:
            return await asyncio.get_running_loop().run_in_executor(None, get, url)
        except HTTPError as ex:
            raise VevalRequestError(
                f"Veval: {what} failed with status {ex.code}{_reason(ex)}", ex.code
            ) from None
        except OSError as ex:
            # URLError (no response) and socket timeouts are both OSErrors.
            raise VevalRequestError(f"Veval: {what} failed: {getattr(ex, 'reason', ex)}") from ex

    async def post_scenario_run_async(self, scenario_name: str, payload: Any) -> None:
        """Same failure behaviour as send_trace_async."""
        encoded = quote(scenario_name, safe="")
        await self._send_async(
            f"Posting scenario run '{scenario_name}'",
            f"{self._endpoint}/v1/scenarios/{encoded}/runs", payload, None,
        )

    async def _send_async(self, what: str, url: str, payload: Any, raise_on_failure: Optional[bool]) -> bool:
        import asyncio
        try:
            await asyncio.get_running_loop().run_in_executor(None, self._post, url, payload)
            return True
        except HTTPError as ex:
            error = VevalSendError(f"Veval: {what} failed with status {ex.code}{_reason(ex)}", ex.code)
        except Exception as ex:
            error = VevalSendError(f"Veval: {what} failed: {ex}")

        _logger.warning(str(error))
        if self._raise_on_send_failure if raise_on_failure is None else raise_on_failure:
            raise error
        return False

    async def judge_async(self, payload: Any) -> dict:
        # Unlike the other calls on this client, judge_async deliberately does not swallow
        # failures — a network/API error here must surface as an assertion failure, not a
        # silent pass. Callers are expected to catch.
        import asyncio
        return await asyncio.get_running_loop().run_in_executor(
            None, self._post_json, f"{self._endpoint}/v1/judge", payload
        )

    async def create_snapshot_async(self, payload: Any) -> SnapshotData:
        # Raises on failure — a baseline that silently didn't save would make every later
        # comparison fail, or compare against a stale one.
        import asyncio
        data = await asyncio.get_running_loop().run_in_executor(
            None, self._post_json, f"{self._endpoint}/v1/snapshots", payload
        )
        return SnapshotData.from_dict(data)

    async def get_snapshot_async(self, name: str) -> Optional[SnapshotData]:
        # Returns None only when no baseline exists (404); any other failure raises, so a network
        # error can never look like "nothing to compare against".
        import asyncio
        encoded = quote(name, safe="")
        data = await asyncio.get_running_loop().run_in_executor(
            None, self._get_or_none_on_404, f"{self._endpoint}/v1/snapshots/{encoded}"
        )
        return SnapshotData.from_dict(data) if data is not None else None

    def _get_or_none_on_404(self, url: str) -> Optional[dict]:
        try:
            return self._get_json(url)
        except HTTPError as ex:
            if ex.code == 404:
                ex.close()
                return None
            raise

    def _get_json(self, url: str) -> dict:
        req = Request(url, headers=self._headers(), method="GET")
        # urlopen raises HTTPError for any non-2xx status and URLError when there's no response.
        with urlopen(req, timeout=10) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def _post_json(self, url: str, payload: Any) -> dict:
        body = json.dumps(payload, default=str).encode("utf-8")
        req = Request(url, data=body, headers=self._headers(), method="POST")
        with urlopen(req, timeout=30) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def _post(self, url: str, payload: Any) -> None:
        body = json.dumps(payload, default=str).encode("utf-8")
        req = Request(url, data=body, headers=self._headers(), method="POST")
        # urlopen raises HTTPError for any non-2xx status, so a 401 or 429 is a failure here too.
        with urlopen(req, timeout=10):
            pass


def _reason(ex: HTTPError) -> str:
    """The response body as ": body", cut to 500 characters, or "" when it's empty. Closes the response."""
    try:
        body = ex.read().decode("utf-8", errors="replace")[:500]
    except Exception:
        # The status alone is enough; a body that can't be read mustn't escape the handler.
        body = ""
    finally:
        ex.close()
    return f": {body}" if body else ""
