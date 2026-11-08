from __future__ import annotations
import math
from dataclasses import dataclass
from typing import Any, Callable, Optional, Union

# What a ReplayFault serves in place of a step's recorded output:
#   "timeout"    — raises a TimeoutError.
#   "http_error" — raises a FaultHttpError with the fault's status.
#   "rate_limit" — raises a FaultHttpError with status 429 and a Retry-After header.
#   "malformed"  — returns a malformed value instead of raising.
#   "custom"     — raises the exception the fault's factory builds.
TIMEOUT = "timeout"
HTTP_ERROR = "http_error"
RATE_LIMIT = "rate_limit"
MALFORMED = "malformed"
CUSTOM = "custom"

# A malformed output a ReplayFault.malformed fault returns:
#   "truncated" — the step's recorded output as JSON text, cut in half. Needs an unused recording of the step.
#   "empty"     — "" when the recorded output is text, None otherwise.
TRUNCATED = "truncated"
EMPTY = "empty"


@dataclass(frozen=True)
class MalformedValue:
    """A malformed output given as a value, e.g. one of the wrong type: ReplayFault.malformed(step, MalformedValue(42))."""
    value: Any


class ReplayFault:
    """Serves a declared failure instead of a step's recorded output, so a replay exercises the retries, fallbacks
    and guards a recording almost never contains. Add faults to ReplayOptions.faults. Deterministic: a fault fires on
    the calls it targets and nowhere else.

    By default a fault fires on every call of its step. times(n) limits it to the first n matching calls, after which
    the step is served from the recording again, which tests recovery. A faulted call never uses up a recording, so a
    retry with the same input gets the recorded output. A fault that never fires fails the replay."""

    def __init__(
        self,
        step_name: str,
        kind: str,
        *,
        status: Optional[int] = None,
        retry_after_seconds: Optional[float] = None,
        malformed_output: Optional[str] = None,
        malformed_value: Any = None,
        exception_factory: Optional[Callable[[], BaseException]] = None,
    ):
        """Use the factory methods (ReplayFault.timeout, http_error, rate_limit, malformed, throw)."""
        if not step_name:
            raise ValueError("A fault needs a step name.")
        self.step_name = step_name
        self.kind = kind
        # The HTTP status of an http_error or rate_limit fault.
        self.status = status
        # The Retry-After of a rate_limit fault, in seconds.
        self.retry_after_seconds = retry_after_seconds
        # What a malformed fault returns ("truncated" or "empty"), unless it returns malformed_value.
        self.malformed_output = malformed_output
        self.malformed_value = malformed_value
        self._exception_factory = exception_factory
        self._count: Optional[int] = None
        self._from_call = 1
        self._has_input = False
        self._input: Any = None

    @staticmethod
    def timeout(step_name: str) -> ReplayFault:
        """The step times out: a TimeoutError."""
        return ReplayFault(step_name, TIMEOUT)

    @staticmethod
    def http_error(step_name: str, status: int) -> ReplayFault:
        """The step fails with an HTTP error status, e.g. 500 or 503 (FaultHttpError)."""
        if not isinstance(status, int) or not 400 <= status <= 599:
            raise ValueError(f"An HTTP error status is between 400 and 599, got {status}.")
        return ReplayFault(step_name, HTTP_ERROR, status=status)

    @staticmethod
    def rate_limit(step_name: str, retry_after_seconds: float = 1) -> ReplayFault:
        """The step is rate limited: status 429 with a Retry-After (one second unless given)."""
        if retry_after_seconds < 0:
            raise ValueError("Retry-After can't be negative.")
        return ReplayFault(step_name, RATE_LIMIT, status=429, retry_after_seconds=retry_after_seconds)

    @staticmethod
    def malformed(step_name: str, output: Union[str, MalformedValue]) -> ReplayFault:
        """The step returns a malformed output instead of raising, so the guard that parses it runs: "truncated" or
        "empty", or any other value (e.g. one of the wrong type) given as MalformedValue(value)."""
        if isinstance(output, MalformedValue):
            return ReplayFault(step_name, MALFORMED, malformed_value=output.value)
        if output not in (TRUNCATED, EMPTY):
            raise ValueError(
                f'A malformed output is "truncated", "empty" or MalformedValue(value), not {output!r}.')
        return ReplayFault(step_name, MALFORMED, malformed_output=output)

    @staticmethod
    def throw(step_name: str, exception_factory: Callable[[], BaseException]) -> ReplayFault:
        """The step raises the exception this factory builds, for clients with their own exception types."""
        if not callable(exception_factory):
            raise TypeError("ReplayFault.throw needs an exception factory.")
        return ReplayFault(step_name, CUSTOM, exception_factory=exception_factory)

    @property
    def count(self) -> Optional[int]:
        """How many matching calls the fault fails; None fails every one."""
        return self._count

    @property
    def from_call(self) -> int:
        """The 1-based call of the step from which the fault applies; earlier calls are served as usual."""
        return self._from_call

    @property
    def has_input(self) -> bool:
        """True when the fault only applies to calls whose input equals input."""
        return self._has_input

    @property
    def input(self) -> Any:
        return self._input

    def times(self, count: int) -> ReplayFault:
        """Fail only the first count matching calls, then serve the recording again."""
        if not isinstance(count, int) or count < 1:
            raise ValueError("A fault fires at least once.")
        self._count = count
        return self

    def on_call(self, call: int) -> ReplayFault:
        """Apply from the call-th call of the step (1-based, retries included)."""
        if not isinstance(call, int) or call < 1:
            raise ValueError("Calls are numbered from 1.")
        self._from_call = call
        return self

    def when_input(self, input: Any) -> ReplayFault:
        """Apply only to calls whose input equals this one, compared as replay matches recordings (key order and the
        step's ignore_input_fields aside)."""
        self._input = input
        self._has_input = True
        return self

    @property
    def description(self) -> str:
        """A short label, e.g. "Timeout on 'search_flights'" or "HTTP 503 on 'book_hotel' (first 2 calls)"."""
        if self.kind == TIMEOUT:
            kind = "Timeout"
        elif self.kind == HTTP_ERROR:
            kind = f"HTTP {self.status}"
        elif self.kind == RATE_LIMIT:
            kind = f"Rate limit (429, Retry-After {_format_seconds(self.retry_after_seconds or 0)}s)"
        elif self.kind == MALFORMED:
            kind = f"Malformed output ({self.malformed_output or 'value'})"
        else:
            kind = "Custom exception"
        scope = []
        if self._count is not None:
            scope.append("first call" if self._count == 1 else f"first {self._count} calls")
        if self._from_call > 1:
            scope.append(f"from call {self._from_call}")
        if self._has_input:
            scope.append("matching input")
        return f"{kind} on '{self.step_name}'" + (f" ({', '.join(scope)})" if scope else "")

    def __repr__(self) -> str:
        return f"ReplayFault({self.description})"

    def _create_exception(self) -> BaseException:
        """The exception a raising fault raises at a call of its step."""
        message = f"Injected fault: {self.description}."
        if self.kind == TIMEOUT:
            return TimeoutError(message)
        if self.kind in (HTTP_ERROR, RATE_LIMIT):
            assert self.status is not None
            return FaultHttpError(message, self.status, self.retry_after_seconds)
        if self.kind == CUSTOM:
            assert self._exception_factory is not None
            exception = self._exception_factory()
            if exception is None:
                raise RuntimeError(f"The exception factory of the fault on '{self.step_name}' returned None.")
            return exception
        raise RuntimeError(f"A {self.kind} fault doesn't raise.")


def _format_seconds(seconds: float) -> str:
    rounded = round(seconds, 3)
    return str(int(rounded)) if rounded == int(rounded) else str(rounded)


class FaultHttpError(Exception):
    """The HTTP failure a ReplayFault.http_error or ReplayFault.rate_limit fault raises. status_code and headers carry
    what the response would have; a rate limit sets headers["Retry-After"]."""

    def __init__(self, message: str, status_code: int, retry_after_seconds: Optional[float] = None):
        super().__init__(message)
        self.status_code = status_code
        # The 429's Retry-After in seconds, or None.
        self.retry_after_seconds = retry_after_seconds
        # The response headers the fault simulates: Retry-After (in whole seconds) for a rate limit.
        self.headers: dict[str, str] = (
            {"Retry-After": str(math.ceil(retry_after_seconds))} if retry_after_seconds is not None else {}
        )


@dataclass(frozen=True)
class FaultReport:
    """How many times one fault fired during a replay."""
    fault: ReplayFault
    fired: int

    @property
    def triggered(self) -> bool:
        return self.fired > 0
