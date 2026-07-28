from __future__ import annotations
import uuid
from datetime import datetime, timezone
from typing import Any, Optional


class Step:
    def __init__(self, name: str, parent_step_id: Optional[str] = None):
        self.step_id: str = str(uuid.uuid4())
        self.parent_step_id: Optional[str] = parent_step_id
        self.name: str = name
        self.type: str = "custom"
        self.input: Any = None
        self.output: Any = None
        self.status: str = "running"
        self.error: Optional[str] = None
        self.started_at: datetime = datetime.now(timezone.utc)
        self.completed_at: Optional[datetime] = None
        self.duration_ms: Optional[int] = None
        self.cost_usd: Optional[float] = None
        self.tokens_in: Optional[int] = None
        self.tokens_out: Optional[int] = None
        self.model: Optional[str] = None
        self.metadata: dict[str, Any] = {}
        self._children: list[Step] = []

    @property
    def children(self) -> list[Step]:
        return self._children

    def complete(self, output: Any = None) -> None:
        self.output = output
        self.status = "success"
        self.completed_at = datetime.now(timezone.utc)
        self.duration_ms = int((self.completed_at - self.started_at).total_seconds() * 1000)

    def fail(self, error: str) -> None:
        self.error = error
        self.status = "error"
        self.completed_at = datetime.now(timezone.utc)
        self.duration_ms = int((self.completed_at - self.started_at).total_seconds() * 1000)

    def create_step(self, name: str) -> Step:
        child = Step(name, self.step_id)
        self._children.append(child)
        return child

    def set_metadata(self, key: str, value: Any) -> None:
        self.metadata[key] = value


class StepHandle:
    def __init__(self, step: Step):
        self._step = step

    def set_meta(self, key: str, value: Any) -> None:
        if key == "tokens_in":
            self._step.tokens_in = int(value)
        elif key == "tokens_out":
            self._step.tokens_out = int(value)
        elif key == "cost_usd":
            self._step.cost_usd = float(value)
        elif key == "model":
            self._step.model = str(value)
        elif key == "type":
            self._step.type = str(value)
        else:
            self._step.set_metadata(key, value)
