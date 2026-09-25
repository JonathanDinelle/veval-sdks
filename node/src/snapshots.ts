import { Step } from "./step";
import { VevalExecutionContext } from "./context";
import type { TraceData } from "./tracing";

export interface SnapshotStep {
  name: string;
  type?: string | null;
  input?: unknown;
  output?: unknown;
}

/**
 * A known-good run to compare new runs against: every step in order, with the input the agent sent and
 * the output it got back. Stored server-side by name (`saveSnapshotAsync`) so it survives trace retention.
 * Legacy snapshots (step names only) have an empty `steps` array and are compared on sequence alone.
 */
export interface SnapshotData {
  id?: string;
  name?: string;
  source_trace_id?: string;
  version?: number;
  created_at?: string;
  steps: SnapshotStep[];
  step_names: string[];
  step_order: string[];
  step_count: number;
}

/** What a snapshot comparison checks beyond the step sequence, which is always compared. */
export interface SnapshotOptions {
  /** Compare what the agent sent to each step (prompts, tool arguments). Default true. */
  compareInputs?: boolean;
  /** Compare each step's output. Default false — live LLM and API outputs vary run to run. */
  compareOutputs?: boolean;
  /** Steps left out of the comparison entirely. */
  ignoreSteps?: string[];
  /** JSON property names dropped from inputs/outputs at any depth before comparing (e.g. "timestamp"). */
  ignoreFields?: string[];
  /** Optional rewrite applied to each input/output before comparing. */
  normalize?: (stepName: string, value: unknown) => unknown;
}

export type SnapshotChangeKind = "Added" | "Removed" | "Moved" | "InputChanged" | "OutputChanged";

export interface SnapshotChange {
  kind: SnapshotChangeKind;
  step_name: string;
  /** Which call of this step name, 1-based — distinguishes repeated calls of the same step. */
  occurrence: number;
  expected_index: number | null;
  actual_index: number | null;
  expected?: string;
  actual?: string;
  /** A short line diff of expected vs actual, for input/output changes. */
  detail: string | null;
}

export interface SnapshotAlignmentRow {
  kind: "match" | "added" | "removed";
  step_name: string;
  expected_index: number | null;
  actual_index: number | null;
  input_changed: boolean;
  output_changed: boolean;
}

export interface SnapshotDiff {
  has_changes: boolean;
  /** Every difference found, in run order. */
  changes: SnapshotChange[];
  /** Expected and actual steps side by side, in run order. */
  alignment: SnapshotAlignmentRow[];
  expected_steps: SnapshotStep[];
  actual_steps: SnapshotStep[];
  /** False for a legacy (names-only) snapshot: only the step sequence could be compared. */
  compared_content: boolean;
  // Name-level views, kept for existing callers.
  added_steps: string[];
  removed_steps: string[];
  order_changes: string[];
  /** A readable report of every change, with input diffs — suitable for a test failure message. */
  summary(snapshotName?: string): string;
}

function collectSteps(steps: readonly Step[]): SnapshotStep[] {
  const result: SnapshotStep[] = [];
  for (const s of steps) {
    result.push({ name: s.name, type: s.type, input: s.input, output: s.output });
    result.push(...collectSteps(s.children));
  }
  return result;
}

function fromSteps(steps: SnapshotStep[], sourceTraceId?: string): SnapshotData {
  const names = steps.map((s) => s.name);
  return {
    version: 2,
    source_trace_id: sourceTraceId,
    steps,
    step_names: [...new Set(names)],
    step_order: names,
    step_count: steps.length,
  };
}

export const SnapshotDataHelper = {
  fromContext(ctx: VevalExecutionContext): SnapshotData {
    return fromSteps(collectSteps(ctx.steps), ctx.traceId);
  },

  fromTrace(trace: TraceData): SnapshotData {
    const data = fromSteps(
      trace.steps.map((s) => ({ name: s.name, type: s.type, input: s.input, output: s.output })),
      trace.trace_id
    );
    return { ...data, id: trace.trace_id };
  },

  /** True when the snapshot recorded steps with inputs/outputs, not just names. */
  hasStepContent(snapshot: SnapshotData): boolean {
    return (snapshot.steps?.length ?? 0) > 0;
  },
};

function orderedSteps(snapshot: SnapshotData): SnapshotStep[] {
  if (snapshot.steps?.length) return snapshot.steps;
  return (snapshot.step_order ?? []).map((name) => ({ name }));
}

function normalizeJson(value: unknown, ignore: Set<string>): unknown {
  if (Array.isArray(value)) return value.map((v) => normalizeJson(v, ignore));
  if (value !== null && typeof value === "object") {
    const sorted: Record<string, unknown> = {};
    for (const key of Object.keys(value as object).sort()) {
      if (ignore.has(key)) continue;
      sorted[key] = normalizeJson((value as Record<string, unknown>)[key], ignore);
    }
    return sorted;
  }
  return value;
}

/** Canonical text for a step input/output: sorted keys, ignored fields dropped, strings as-is. */
function canonicalize(value: unknown, ignore: Set<string>): string {
  if (value === null || value === undefined) return "null";
  if (typeof value === "string") return value;
  return JSON.stringify(normalizeJson(value, ignore), null, 2);
}

/**
 * A readable rendering for diffs: sorted keys, one value per line, and multi-line strings (prompts)
 * expanded line by line — so editing one line of a prompt diffs as one line, not one giant JSON string.
 */
function display(value: unknown, ignore: Set<string>): string {
  if (value === null || value === undefined) return "null";
  if (typeof value === "string") return value;
  const out: string[] = [];
  render(normalizeJson(value, ignore), 0, out);
  return out.join("\n");
}

function isNonEmptyContainer(v: unknown): boolean {
  return Array.isArray(v) ? v.length > 0 : v !== null && typeof v === "object" && Object.keys(v as object).length > 0;
}

function scalar(v: unknown): string {
  if (v === null || v === undefined) return "null";
  if (typeof v === "string") return v;
  if (Array.isArray(v)) return "[]";
  if (typeof v === "object") return "{}";
  return JSON.stringify(v);
}

function render(node: unknown, indent: number, out: string[]): void {
  const pad = " ".repeat(indent);
  if (Array.isArray(node) && node.length > 0) {
    for (const item of node) renderEntry(pad + "-", item, indent, out);
  } else if (isNonEmptyContainer(node)) {
    for (const [k, v] of Object.entries(node as Record<string, unknown>)) renderEntry(pad + k + ":", v, indent, out);
  } else {
    out.push(pad + scalar(node));
  }
}

function renderEntry(label: string, value: unknown, indent: number, out: string[]): void {
  if (isNonEmptyContainer(value)) {
    out.push(label);
    render(value, indent + 2, out);
  } else if (typeof value === "string" && value.includes("\n")) {
    out.push(label + " |");
    for (const line of value.replace(/\r\n/g, "\n").split("\n")) out.push(" ".repeat(indent + 2) + line);
  } else {
    out.push(label + " " + scalar(value));
  }
}

/** A compact line diff (expected "-", actual "+") around the first differing lines. */
function lineDiff(expected: string, actual: string, context = 1, maxLines = 12): string {
  const e = expected.replace(/\r\n/g, "\n").split("\n");
  const a = actual.replace(/\r\n/g, "\n").split("\n");
  let prefix = 0;
  while (prefix < e.length && prefix < a.length && e[prefix] === a[prefix]) prefix++;
  let suffix = 0;
  while (suffix < e.length - prefix && suffix < a.length - prefix && e[e.length - 1 - suffix] === a[a.length - 1 - suffix]) suffix++;

  const out: string[] = [];
  let lines = 0;
  const add = (line: string) => {
    if (lines++ < maxLines) out.push(line);
  };
  for (let i = Math.max(0, prefix - context); i < prefix; i++) add("  " + e[i]);
  for (let i = prefix; i < e.length - suffix; i++) add("- " + e[i]);
  for (let i = prefix; i < a.length - suffix; i++) add("+ " + a[i]);
  for (let i = a.length - suffix; i < Math.min(a.length, a.length - suffix + context); i++) add("  " + a[i]);
  if (lines > maxLines) out.push(`  … ${lines - maxLines} more line(s)`);
  return out.join("\n");
}

function occurrences(steps: SnapshotStep[]): number[] {
  const counts = new Map<string, number>();
  return steps.map((s) => {
    const n = (counts.get(s.name) ?? 0) + 1;
    counts.set(s.name, n);
    return n;
  });
}

function align(expected: SnapshotStep[], actual: SnapshotStep[]): SnapshotAlignmentRow[] {
  const n = expected.length;
  const m = actual.length;
  const lcs = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      lcs[i][j] = expected[i].name === actual[j].name ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);

  const rows: SnapshotAlignmentRow[] = [];
  const row = (kind: SnapshotAlignmentRow["kind"], name: string, ei: number | null, ai: number | null): SnapshotAlignmentRow =>
    ({ kind, step_name: name, expected_index: ei, actual_index: ai, input_changed: false, output_changed: false });
  let x = 0;
  let y = 0;
  while (x < n || y < m) {
    if (x < n && y < m && expected[x].name === actual[y].name) {
      rows.push(row("match", expected[x].name, x, y));
      x++;
      y++;
    } else if (y < m && (x === n || lcs[x][y + 1] >= lcs[x + 1][y])) {
      rows.push(row("added", actual[y].name, null, y));
      y++;
    } else {
      rows.push(row("removed", expected[x].name, x, null));
      x++;
    }
  }
  return rows;
}

function label(c: SnapshotChange): string {
  return c.occurrence > 1 ? `'${c.step_name}' (call #${c.occurrence})` : `'${c.step_name}'`;
}

export function describeChange(c: SnapshotChange): string {
  switch (c.kind) {
    case "Added": return `+ added ${label(c)} at position ${c.actual_index}`;
    case "Removed": return `- removed ${label(c)} (was at position ${c.expected_index})`;
    case "Moved": return `~ moved ${label(c)} from position ${c.expected_index} to ${c.actual_index}`;
    case "InputChanged": return `~ input changed for ${label(c)}`;
    case "OutputChanged": return `~ output changed for ${label(c)}`;
  }
}

export const SnapshotComparer = {
  compare(snapshot: SnapshotData, current: VevalExecutionContext | SnapshotData, options: SnapshotOptions = {}): SnapshotDiff {
    const actualData = current instanceof VevalExecutionContext ? SnapshotDataHelper.fromContext(current) : current;
    const ignoreSteps = new Set(options.ignoreSteps ?? []);
    const ignoreFields = new Set(options.ignoreFields ?? []);
    const compareInputs = options.compareInputs ?? true;
    const compareOutputs = options.compareOutputs ?? false;

    const expected = orderedSteps(snapshot).filter((s) => !ignoreSteps.has(s.name));
    const actual = orderedSteps(actualData).filter((s) => !ignoreSteps.has(s.name));
    const compareContent = SnapshotDataHelper.hasStepContent(snapshot);
    const expectedOcc = occurrences(expected);
    const actualOcc = occurrences(actual);

    // Align on step name (longest common subsequence), so an inserted, dropped, repeated,
    // or reordered step shows up exactly where it happened.
    const rows = align(expected, actual);

    // A step removed in one place and added in another has moved.
    const removed = rows.filter((r) => r.kind === "removed");
    const added = rows.filter((r) => r.kind === "added");
    const moves = new Map<SnapshotAlignmentRow, SnapshotAlignmentRow>();
    for (const r of [...removed]) {
      const match = added.find((a) => a.step_name === r.step_name);
      if (!match) continue;
      moves.set(match, r);
      removed.splice(removed.indexOf(r), 1);
      added.splice(added.indexOf(match), 1);
    }

    const changes: SnapshotChange[] = [];
    const differs = (name: string, e: unknown, a: unknown) => {
      if (options.normalize) {
        e = options.normalize(name, e);
        a = options.normalize(name, a);
      }
      if (canonicalize(e, ignoreFields) === canonicalize(a, ignoreFields)) return null;
      // Equality is decided on canonical JSON; the reported values use the readable form for diffing.
      return { et: display(e, ignoreFields), at: display(a, ignoreFields) };
    };

    for (const r of rows) {
      if (r.kind === "removed" && removed.includes(r)) {
        changes.push({ kind: "Removed", step_name: r.step_name, occurrence: expectedOcc[r.expected_index!],
          expected_index: r.expected_index, actual_index: null, detail: null });
      } else if (r.kind === "added" && added.includes(r)) {
        changes.push({ kind: "Added", step_name: r.step_name, occurrence: actualOcc[r.actual_index!],
          expected_index: null, actual_index: r.actual_index, detail: null });
      } else if (r.kind === "added" && moves.has(r)) {
        changes.push({ kind: "Moved", step_name: r.step_name, occurrence: actualOcc[r.actual_index!],
          expected_index: moves.get(r)!.expected_index, actual_index: r.actual_index, detail: null });
      }

      if (compareContent && r.kind === "match") {
        const e = expected[r.expected_index!];
        const a = actual[r.actual_index!];
        const content: Array<[boolean, SnapshotChangeKind, unknown, unknown]> = [
          [compareInputs, "InputChanged", e.input, a.input],
          [compareOutputs, "OutputChanged", e.output, a.output],
        ];
        for (const [enabled, kind, ev, av] of content) {
          if (!enabled) continue;
          const d = differs(e.name, ev, av);
          if (!d) continue;
          if (kind === "InputChanged") r.input_changed = true;
          else r.output_changed = true;
          changes.push({ kind, step_name: r.step_name, occurrence: actualOcc[r.actual_index!],
            expected_index: r.expected_index, actual_index: r.actual_index,
            expected: d.et, actual: d.at, detail: lineDiff(d.et, d.at) });
        }
      }
    }

    return {
      has_changes: changes.length > 0,
      changes,
      alignment: rows,
      expected_steps: expected,
      actual_steps: actual,
      compared_content: compareContent,
      added_steps: changes.filter((c) => c.kind === "Added").map((c) => c.step_name),
      removed_steps: changes.filter((c) => c.kind === "Removed").map((c) => c.step_name),
      order_changes: changes.filter((c) => c.kind === "Moved")
        .map((c) => `'${c.step_name}' moved from position ${c.expected_index} to ${c.actual_index}`),
      summary(snapshotName?: string) {
        const title = snapshotName ? `Snapshot '${snapshotName}'` : "Snapshot";
        if (changes.length === 0) return `${title}: no changes.`;
        const lines = [`${title}: ${changes.length} change(s)`];
        for (const c of changes) {
          lines.push("  " + describeChange(c));
          if (c.detail) for (const l of c.detail.split("\n")) lines.push("      " + l);
        }
        if (!compareContent)
          lines.push("  (legacy snapshot: step inputs were not recorded, so only the sequence was compared)");
        return lines.join("\n");
      },
    };
  },
};

const step = (s: SnapshotStep) => ({ name: s.name, type: s.type ?? null, input: s.input ?? null, output: s.output ?? null });

/** Wire shapes for snapshot calls. */
export const SnapshotPayloads = {
  saveFromTrace: (name: string, traceId: string) => ({ name, trace_id: traceId }),

  saveFromContext: (name: string, ctx: VevalExecutionContext) => ({
    name,
    source_trace_id: ctx.traceId,
    steps: SnapshotDataHelper.fromContext(ctx).steps.map(step),
  }),

  /** A scenario-run body the dashboard renders as a step-by-step snapshot diff. */
  run: (snapshotName: string, diff: SnapshotDiff) => ({
    passed: !diff.has_changes,
    pass_count: diff.has_changes ? 0 : 1,
    fail_count: diff.has_changes ? 1 : 0,
    results: [{
      name: snapshotName,
      passed: !diff.has_changes,
      type: "snapshot",
      expected: diff.expected_steps.map(step),
      actual: diff.actual_steps.map(step),
      alignment: diff.alignment,
      changes: diff.changes.map(({ expected, actual, ...rest }) => rest),
      failures: diff.changes.map(describeChange),
    }],
  }),
};
