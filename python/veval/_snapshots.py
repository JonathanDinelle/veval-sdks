from __future__ import annotations
import json
from dataclasses import dataclass, field
from typing import Any, Callable, Optional, Union, TYPE_CHECKING

if TYPE_CHECKING:
    from ._context import VevalExecutionContext
    from ._step import Step
    from ._tracing import TraceData


@dataclass
class SnapshotStep:
    name: str = ""
    type: Optional[str] = None
    input: Any = None
    output: Any = None


@dataclass
class SnapshotData:
    """
    A known-good run to compare new runs against: every step in order, with the input the agent sent
    and the output it got back. Stored server-side by name (``save_snapshot_async``) so it survives
    trace retention. Legacy snapshots (step names only) have no ``steps`` and are compared on sequence alone.
    """
    id: Optional[str] = None
    name: Optional[str] = None
    source_trace_id: Optional[str] = None
    version: int = 2
    created_at: Optional[str] = None
    steps: list[SnapshotStep] = field(default_factory=list)
    step_names: list[str] = field(default_factory=list)
    step_order: list[str] = field(default_factory=list)
    step_count: int = 0

    @property
    def has_step_content(self) -> bool:
        """True when the snapshot recorded steps with inputs/outputs, not just names."""
        return len(self.steps) > 0

    def ordered_steps(self) -> list[SnapshotStep]:
        if self.steps or not self.step_order:
            return self.steps
        return [SnapshotStep(name=n) for n in self.step_order]

    @staticmethod
    def from_context(ctx: VevalExecutionContext) -> SnapshotData:
        steps: list[SnapshotStep] = []
        _collect_steps(ctx.steps, steps)
        return _from_steps(steps, source_trace_id=ctx.trace_id)

    @staticmethod
    def from_trace(trace: TraceData) -> SnapshotData:
        steps = [SnapshotStep(name=s.name, type=s.type, input=s.input, output=s.output) for s in trace.steps]
        data = _from_steps(steps, source_trace_id=trace.trace_id)
        data.id = trace.trace_id
        return data

    @staticmethod
    def from_dict(data: dict) -> SnapshotData:
        return SnapshotData(
            id=data.get("id"),
            name=data.get("name"),
            source_trace_id=data.get("source_trace_id"),
            version=data.get("version") or 1,
            created_at=data.get("created_at"),
            steps=[
                SnapshotStep(name=s.get("name", ""), type=s.get("type"), input=s.get("input"), output=s.get("output"))
                for s in data.get("steps") or []
            ],
            step_names=list(data.get("step_names") or []),
            step_order=list(data.get("step_order") or []),
            step_count=data.get("step_count") or 0,
        )


def _collect_steps(steps: list[Step], result: list[SnapshotStep]) -> None:
    for s in steps:
        result.append(SnapshotStep(name=s.name, type=s.type, input=s.input, output=s.output))
        _collect_steps(s.children, result)


def _from_steps(steps: list[SnapshotStep], source_trace_id: Optional[str]) -> SnapshotData:
    names = [s.name for s in steps]
    return SnapshotData(
        source_trace_id=source_trace_id,
        steps=steps,
        step_names=list(dict.fromkeys(names)),
        step_order=names,
        step_count=len(steps),
    )


@dataclass
class SnapshotOptions:
    """What a snapshot comparison checks beyond the step sequence, which is always compared."""
    # Compare what the agent sent to each step (prompts, tool arguments).
    compare_inputs: bool = True
    # Compare each step's output. Off by default — live LLM and API outputs vary run to run.
    compare_outputs: bool = False
    # Steps left out of the comparison entirely.
    ignore_steps: set[str] = field(default_factory=set)
    # JSON property names dropped from inputs/outputs at any depth before comparing (e.g. "timestamp").
    ignore_fields: set[str] = field(default_factory=set)
    # Optional rewrite applied to each input/output before comparing: (step_name, value) -> value.
    normalize: Optional[Callable[[str, Any], Any]] = None


@dataclass
class SnapshotChange:
    kind: str  # "Added" | "Removed" | "Moved" | "InputChanged" | "OutputChanged"
    step_name: str
    # Which call of this step name, 1-based — distinguishes repeated calls of the same step.
    occurrence: int = 1
    expected_index: Optional[int] = None
    actual_index: Optional[int] = None
    expected: Optional[str] = None
    actual: Optional[str] = None
    # A short line diff of expected vs actual, for input/output changes.
    detail: Optional[str] = None

    @property
    def label(self) -> str:
        return f"'{self.step_name}' (call #{self.occurrence})" if self.occurrence > 1 else f"'{self.step_name}'"

    def __str__(self) -> str:
        if self.kind == "Added":
            return f"+ added {self.label} at position {self.actual_index}"
        if self.kind == "Removed":
            return f"- removed {self.label} (was at position {self.expected_index})"
        if self.kind == "Moved":
            return f"~ moved {self.label} from position {self.expected_index} to {self.actual_index}"
        if self.kind == "InputChanged":
            return f"~ input changed for {self.label}"
        return f"~ output changed for {self.label}"


@dataclass
class SnapshotAlignmentRow:
    kind: str  # "match" | "added" | "removed"
    step_name: str
    expected_index: Optional[int] = None
    actual_index: Optional[int] = None
    input_changed: bool = False
    output_changed: bool = False


@dataclass
class SnapshotDiff:
    # Every difference found, in run order.
    changes: list[SnapshotChange] = field(default_factory=list)
    # Expected and actual steps side by side, in run order.
    alignment: list[SnapshotAlignmentRow] = field(default_factory=list)
    expected_steps: list[SnapshotStep] = field(default_factory=list)
    actual_steps: list[SnapshotStep] = field(default_factory=list)
    # False for a legacy (names-only) snapshot: only the step sequence could be compared.
    compared_content: bool = True
    # Name-level views, kept for existing callers.
    added_steps: list[str] = field(default_factory=list)
    removed_steps: list[str] = field(default_factory=list)
    order_changes: list[str] = field(default_factory=list)

    @property
    def has_changes(self) -> bool:
        return len(self.changes) > 0

    def summary(self, snapshot_name: Optional[str] = None) -> str:
        """A readable report of every change, with input diffs — suitable for a test failure message."""
        title = f"Snapshot '{snapshot_name}'" if snapshot_name else "Snapshot"
        if not self.changes:
            return f"{title}: no changes."
        lines = [f"{title}: {len(self.changes)} change(s)"]
        for c in self.changes:
            lines.append(f"  {c}")
            if c.detail:
                lines.extend(f"      {line}" for line in c.detail.split("\n"))
        if not self.compared_content:
            lines.append("  (legacy snapshot: step inputs were not recorded, so only the sequence was compared)")
        return "\n".join(lines)

    def __str__(self) -> str:
        return self.summary()


def _normalize_json(value: Any, ignore: set[str]) -> Any:
    if isinstance(value, dict):
        return {k: _normalize_json(v, ignore) for k, v in value.items() if k not in ignore}
    if isinstance(value, (list, tuple)):
        return [_normalize_json(v, ignore) for v in value]
    return value


def _canonicalize(value: Any, ignore: set[str]) -> str:
    """Canonical text for a step input/output: sorted keys, ignored fields dropped, strings as-is."""
    if value is None:
        return "null"
    if isinstance(value, str):
        return value
    if hasattr(value, "__dict__") and not isinstance(value, (dict, list, tuple)):
        value = vars(value)
    return json.dumps(_normalize_json(value, ignore), indent=2, sort_keys=True, default=str, ensure_ascii=False)


def _display(value: Any, ignore: set[str]) -> str:
    """
    A readable rendering for diffs: sorted keys, one value per line, and multi-line strings (prompts)
    expanded line by line — so editing one line of a prompt diffs as one line, not one giant JSON string.
    """
    if value is None:
        return "null"
    if isinstance(value, str):
        return value
    if hasattr(value, "__dict__") and not isinstance(value, (dict, list, tuple)):
        value = vars(value)
    out: list[str] = []
    _render(json.loads(json.dumps(_normalize_json(value, ignore), sort_keys=True, default=str)), 0, out)
    return "\n".join(out)


def _is_nonempty_container(v: Any) -> bool:
    return isinstance(v, (dict, list)) and len(v) > 0


def _scalar(v: Any) -> str:
    if v is None:
        return "null"
    if isinstance(v, str):
        return v
    if isinstance(v, list):
        return "[]"
    if isinstance(v, dict):
        return "{}"
    return json.dumps(v)


def _render(node: Any, indent: int, out: list[str]) -> None:
    pad = " " * indent
    if isinstance(node, list) and node:
        for item in node:
            _render_entry(pad + "-", item, indent, out)
    elif isinstance(node, dict) and node:
        for k, v in node.items():
            _render_entry(f"{pad}{k}:", v, indent, out)
    else:
        out.append(pad + _scalar(node))


def _render_entry(label: str, value: Any, indent: int, out: list[str]) -> None:
    if _is_nonempty_container(value):
        out.append(label)
        _render(value, indent + 2, out)
    elif isinstance(value, str) and "\n" in value:
        out.append(label + " |")
        out.extend(" " * (indent + 2) + line for line in value.replace("\r\n", "\n").split("\n"))
    else:
        out.append(f"{label} {_scalar(value)}")


def _line_diff(expected: str, actual: str, context: int = 1, max_lines: int = 12) -> str:
    """A compact line diff (expected "-", actual "+") around the first differing lines."""
    e = expected.replace("\r\n", "\n").split("\n")
    a = actual.replace("\r\n", "\n").split("\n")
    prefix = 0
    while prefix < len(e) and prefix < len(a) and e[prefix] == a[prefix]:
        prefix += 1
    suffix = 0
    while suffix < len(e) - prefix and suffix < len(a) - prefix and e[len(e) - 1 - suffix] == a[len(a) - 1 - suffix]:
        suffix += 1

    candidates = (
        ["  " + line for line in e[max(0, prefix - context):prefix]]
        + ["- " + line for line in e[prefix:len(e) - suffix]]
        + ["+ " + line for line in a[prefix:len(a) - suffix]]
        + ["  " + line for line in a[len(a) - suffix:min(len(a), len(a) - suffix + context)]]
    )
    out = candidates[:max_lines]
    if len(candidates) > max_lines:
        out.append(f"  … {len(candidates) - max_lines} more line(s)")
    return "\n".join(out)


def _occurrences(steps: list[SnapshotStep]) -> list[int]:
    counts: dict[str, int] = {}
    result = []
    for s in steps:
        counts[s.name] = counts.get(s.name, 0) + 1
        result.append(counts[s.name])
    return result


def _align(expected: list[SnapshotStep], actual: list[SnapshotStep]) -> list[SnapshotAlignmentRow]:
    n, m = len(expected), len(actual)
    lcs = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n - 1, -1, -1):
        for j in range(m - 1, -1, -1):
            lcs[i][j] = lcs[i + 1][j + 1] + 1 if expected[i].name == actual[j].name else max(lcs[i + 1][j], lcs[i][j + 1])

    rows: list[SnapshotAlignmentRow] = []
    x = y = 0
    while x < n or y < m:
        if x < n and y < m and expected[x].name == actual[y].name:
            rows.append(SnapshotAlignmentRow("match", expected[x].name, x, y))
            x += 1
            y += 1
        elif y < m and (x == n or lcs[x][y + 1] >= lcs[x + 1][y]):
            rows.append(SnapshotAlignmentRow("added", actual[y].name, None, y))
            y += 1
        else:
            rows.append(SnapshotAlignmentRow("removed", expected[x].name, x, None))
            x += 1
    return rows


def compare_snapshots(
    snapshot: SnapshotData,
    current: Union[VevalExecutionContext, SnapshotData],
    options: Optional[SnapshotOptions] = None,
) -> SnapshotDiff:
    """Compares a run (or another snapshot) against a baseline: step sequence, plus step inputs/outputs."""
    options = options or SnapshotOptions()
    actual_data = current if isinstance(current, SnapshotData) else SnapshotData.from_context(current)
    expected = [s for s in snapshot.ordered_steps() if s.name not in options.ignore_steps]
    actual = [s for s in actual_data.ordered_steps() if s.name not in options.ignore_steps]
    compare_content = snapshot.has_step_content
    expected_occ = _occurrences(expected)
    actual_occ = _occurrences(actual)

    # Align on step name (longest common subsequence), so an inserted, dropped, repeated,
    # or reordered step shows up exactly where it happened.
    rows = _align(expected, actual)
    diff = SnapshotDiff(alignment=rows, expected_steps=expected, actual_steps=actual, compared_content=compare_content)

    # A step removed in one place and added in another has moved.
    removed = [r for r in rows if r.kind == "removed"]
    added = [r for r in rows if r.kind == "added"]
    moves: dict[int, SnapshotAlignmentRow] = {}
    for r in list(removed):
        match = next((a for a in added if a.step_name == r.step_name), None)
        if match is None:
            continue
        moves[id(match)] = r
        removed.remove(r)
        added.remove(match)

    def differs(name: str, e: Any, a: Any) -> Optional[tuple[str, str]]:
        if options.normalize:
            e, a = options.normalize(name, e), options.normalize(name, a)
        if _canonicalize(e, options.ignore_fields) == _canonicalize(a, options.ignore_fields):
            return None
        # Equality is decided on canonical JSON; the reported values use the readable form for diffing.
        return _display(e, options.ignore_fields), _display(a, options.ignore_fields)

    for r in rows:
        if r.kind == "removed" and any(r is x for x in removed):
            diff.changes.append(SnapshotChange("Removed", r.step_name, expected_occ[r.expected_index], expected_index=r.expected_index))
            diff.removed_steps.append(r.step_name)
        elif r.kind == "added" and any(r is x for x in added):
            diff.changes.append(SnapshotChange("Added", r.step_name, actual_occ[r.actual_index], actual_index=r.actual_index))
            diff.added_steps.append(r.step_name)
        elif r.kind == "added" and id(r) in moves:
            origin = moves[id(r)]
            diff.changes.append(SnapshotChange(
                "Moved", r.step_name, actual_occ[r.actual_index],
                expected_index=origin.expected_index, actual_index=r.actual_index,
            ))
            diff.order_changes.append(f"'{r.step_name}' moved from position {origin.expected_index} to {r.actual_index}")

        if compare_content and r.kind == "match":
            e_step, a_step = expected[r.expected_index], actual[r.actual_index]
            for enabled, kind, ev, av in (
                (options.compare_inputs, "InputChanged", e_step.input, a_step.input),
                (options.compare_outputs, "OutputChanged", e_step.output, a_step.output),
            ):
                if not enabled:
                    continue
                d = differs(e_step.name, ev, av)
                if d is None:
                    continue
                if kind == "InputChanged":
                    r.input_changed = True
                else:
                    r.output_changed = True
                diff.changes.append(SnapshotChange(
                    kind, r.step_name, actual_occ[r.actual_index],
                    expected_index=r.expected_index, actual_index=r.actual_index,
                    expected=d[0], actual=d[1], detail=_line_diff(d[0], d[1]),
                ))

    return diff


def _step_payload(s: SnapshotStep) -> dict:
    return {"name": s.name, "type": s.type, "input": s.input, "output": s.output}


def save_from_trace_payload(name: str, trace_id: str) -> dict:
    return {"name": name, "trace_id": trace_id}


def save_from_context_payload(name: str, ctx: VevalExecutionContext) -> dict:
    return {
        "name": name,
        "source_trace_id": ctx.trace_id,
        "steps": [_step_payload(s) for s in SnapshotData.from_context(ctx).steps],
    }


def snapshot_run_payload(snapshot_name: str, diff: SnapshotDiff) -> dict:
    """A scenario-run body the dashboard renders as a step-by-step snapshot diff."""
    return {
        "passed": not diff.has_changes,
        "pass_count": 0 if diff.has_changes else 1,
        "fail_count": 1 if diff.has_changes else 0,
        "results": [{
            "name": snapshot_name,
            "passed": not diff.has_changes,
            "type": "snapshot",
            "expected": [_step_payload(s) for s in diff.expected_steps],
            "actual": [_step_payload(s) for s in diff.actual_steps],
            "alignment": [vars(r) for r in diff.alignment],
            "changes": [
                {
                    "kind": c.kind, "step_name": c.step_name, "occurrence": c.occurrence,
                    "expected_index": c.expected_index, "actual_index": c.actual_index, "detail": c.detail,
                }
                for c in diff.changes
            ],
            "failures": [str(c) for c in diff.changes],
        }],
    }
