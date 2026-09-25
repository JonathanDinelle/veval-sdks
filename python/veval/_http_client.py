from __future__ import annotations
import json
from typing import Any, Optional
from urllib.parse import quote
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError
from ._tracing import TraceData
from ._snapshots import SnapshotData


class VevalHttpClient:
    def __init__(self, api_key: str, endpoint: str):
        self._api_key = api_key
        self._endpoint = endpoint.rstrip("/")

    def _headers(self) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {self._api_key}",
            "Content-Type": "application/json",
        }

    async def send_trace_async(self, payload: Any) -> None:
        try:
            import asyncio
            await asyncio.get_event_loop().run_in_executor(
                None, self._post, f"{self._endpoint}/v1/traces", payload
            )
        except Exception:
            pass

    async def get_trace_async(self, trace_id: str) -> Optional[TraceData]:
        try:
            import asyncio
            data = await asyncio.get_event_loop().run_in_executor(
                None, self._get, f"{self._endpoint}/v1/traces/{trace_id}"
            )
            return TraceData.from_dict(data) if data else None
        except Exception:
            return None

    async def get_scenario_items_async(self, scenario_name: str) -> list[dict]:
        try:
            import asyncio
            encoded = quote(scenario_name, safe="")
            data = await asyncio.get_event_loop().run_in_executor(
                None, self._get, f"{self._endpoint}/v1/scenarios/{encoded}/items"
            )
            return data.get("items", []) if data else []
        except Exception:
            return []

    async def post_scenario_run_async(self, scenario_name: str, payload: Any) -> None:
        try:
            import asyncio
            encoded = quote(scenario_name, safe="")
            await asyncio.get_event_loop().run_in_executor(
                None, self._post, f"{self._endpoint}/v1/scenarios/{encoded}/runs", payload
            )
        except Exception:
            pass

    async def judge_async(self, payload: Any) -> dict:
        # Unlike the other calls on this client, judge_async deliberately does not swallow
        # failures — a network/API error here must surface as an assertion failure, not a
        # silent pass. Callers are expected to catch.
        import asyncio
        return await asyncio.get_event_loop().run_in_executor(
            None, self._post_json, f"{self._endpoint}/v1/judge", payload
        )

    async def create_snapshot_async(self, payload: Any) -> SnapshotData:
        # Raises on failure — a baseline that silently didn't save would make every later
        # comparison fail, or compare against a stale one.
        import asyncio
        data = await asyncio.get_event_loop().run_in_executor(
            None, self._post_json, f"{self._endpoint}/v1/snapshots", payload
        )
        return SnapshotData.from_dict(data)

    async def get_snapshot_async(self, name: str) -> Optional[SnapshotData]:
        # Returns None only when no baseline exists (404); any other failure raises, so a network
        # error can never look like "nothing to compare against".
        import asyncio
        encoded = quote(name, safe="")
        data = await asyncio.get_event_loop().run_in_executor(
            None, self._get_or_none_on_404, f"{self._endpoint}/v1/snapshots/{encoded}"
        )
        return SnapshotData.from_dict(data) if data is not None else None

    def _get_or_none_on_404(self, url: str) -> Optional[dict]:
        req = Request(url, headers=self._headers(), method="GET")
        try:
            with urlopen(req, timeout=10) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except HTTPError as ex:
            if ex.code == 404:
                return None
            raise

    def _post_json(self, url: str, payload: Any) -> dict:
        body = json.dumps(payload, default=str).encode("utf-8")
        req = Request(url, data=body, headers=self._headers(), method="POST")
        with urlopen(req, timeout=30) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def _post(self, url: str, payload: Any) -> None:
        body = json.dumps(payload, default=str).encode("utf-8")
        req = Request(url, data=body, headers=self._headers(), method="POST")
        try:
            with urlopen(req, timeout=10):
                pass
        except URLError:
            pass

    def _get(self, url: str) -> Optional[dict]:
        req = Request(url, headers=self._headers(), method="GET")
        try:
            with urlopen(req, timeout=10) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except URLError:
            return None
