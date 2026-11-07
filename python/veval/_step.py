from __future__ import annotations
from dataclasses import dataclass
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


class StepType:
    """Step types Veval knows. Step.type is a string, so these are its usual values."""
    LLM = "llm"  # a model call: live replay runs it for real
    TOOL = "tool"  # a tool call: served from the recording; a live replay stops when its input diverges
    CUSTOM = "custom"  # anything else; the default


@dataclass
class StepOptions:
    """Settings for one step, given where the step is called.

    type: the step's type (StepType), known before the step runs. A live replay uses it when the recording doesn't
    know the step's type: a step it never contained, or one saved untyped ("custom"). A model step ("llm") then runs
    live instead of stopping the replay or being served its old output. A recorded "llm" or "tool" type wins, and a
    different type here is reported on the binding (declared_type), so a mislabelled call can't turn a recorded tool
    step live. New recordings carry this type; set_meta("type", ...) inside the step still overrides it."""
    type: "str | None" = None

    def __post_init__(self) -> None:
        if self.type is not None and not isinstance(self.type, str):
            raise TypeError(f"StepOptions.type must be a string such as StepType.LLM, not {type(self.type).__name__}")

