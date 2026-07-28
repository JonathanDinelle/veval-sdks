from __future__ import annotations
import inspect
from collections import deque
from typing import Any, Callable, Awaitable, Optional, TypeVar, TYPE_CHECKING
from ._step import Step, StepHandle

if TYPE_CHECKING:
    from ._tracing import TraceData

T = TypeVar("T")


class VevalExecutionContext:
    def __init__(self, trace_id: str, input: Any = None):
        self.trace_id = trace_id
        self.input = input
        self._steps: list[Step] = []
        self._metadata: dict[str, Any] = {}
        self._mock_outputs: Optional[dict[str, deque]] = None
        self._strict_mock_mode = False

    @property
    def steps(self) -> list[Step]:
        return self._steps

    @property
    def trace_meta(self) -> dict[str, Any]:
        return self._metadata

    async def track_step_async(
        self,
        name: str,
        input: Any,
        step: Callable[..., Awaitable[T]],
    ) -> T:
        if self._mock_outputs is not None and self._strict_mock_mode:
            q = self._mock_outputs.get(name)
            if not q:
                available = ", ".join(self._mock_outputs.keys())
                raise RuntimeError(
                    f"Replay mode: no mock output for step '{name}'. "
                    f"Available steps: {available}. "
                    "This would have made a real LLM call."
                )

        if self._mock_outputs is not None:
            q = self._mock_outputs.get(name)
            if q:
                mock = q.popleft()
                s = Step(name)
                s.input = input
                s.metadata["_source"] = "replay"
                s.complete(mock)
                self._steps.append(s)
                return mock  # type: ignore[return-value]

        s = Step(name)
        s.input = input
        self._steps.append(s)
        handle = StepHandle(s)

        try:
            sig = inspect.signature(step)
            if len(sig.parameters) > 0:
                result = await step(handle)
            else:
                result = await step()  # type: ignore[call-arg]
            s.complete(result)
            return result
        except Exception as ex:
            s.fail(str(ex))
            raise

    def set_metadata(self, key: str, value: Any) -> None:
        self._metadata[key] = value

    def load_mock_outputs(self, trace: TraceData, strict: bool = True) -> None:
        if not trace.steps:
            raise RuntimeError(
                f"Replay trace '{trace.trace_id}' has no steps. Cannot mock LLM calls. "
                "Ensure the trace was recorded with steps before using it for replay."
            )
        self._mock_outputs = {}
        self._strict_mock_mode = strict
        for step in trace.steps:
            if step.name not in self._mock_outputs:
                self._mock_outputs[step.name] = deque()
            self._mock_outputs[step.name].append(step.output)
