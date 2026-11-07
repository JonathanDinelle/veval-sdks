// A saved baseline is pinned unless the caller opts out, and a save the API refuses throws.
// Mirrors sdk/python/tests/test_save_snapshot.py and sdk/dotnet/Veval.Sdk.Tests/SaveSnapshotTests.cs.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { VevalRequestError, VevalSdk } from "../src";
import { run } from "./helpers";

const PIN_LIMIT_BODY =
  '{"error":"pinned snapshot limit reached (3 of 3 on the free plan) — unpin one or upgrade"}';

// Answers every request with `status` and `responseBody`, and returns the JSON bodies it received.
async function withApi(
  body: (sdk: VevalSdk) => Promise<void>,
  status = 200,
  responseBody = '{"name":"baseline","steps":[]}'
): Promise<Record<string, unknown>[]> {
  const received: Record<string, unknown>[] = [];
  const server = createServer((req, res) => {
    let raw = "";
    req.on("data", (chunk) => { raw += chunk; });
    req.on("end", () => {
      if (raw) received.push(JSON.parse(raw));
      res.writeHead(status, { "Content-Type": "application/json" }).end(responseBody);
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  const previous = process.env.VEVAL_INTERNAL_ENDPOINT;
  process.env.VEVAL_INTERNAL_ENDPOINT = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  try {
    await body(new VevalSdk({ apiKey: "key" }));
  } finally {
    process.env.VEVAL_INTERNAL_ENDPOINT = previous;
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
  }
  return received;
}

test("saving from a trace pins the baseline by default", async () => {
  const received = await withApi(async (sdk) => { await sdk.saveSnapshotAsync("baseline", "tr_1"); });

  assert.deepEqual(received, [{ name: "baseline", trace_id: "tr_1", pinned: true }]);
});

test("saving from a run pins the baseline by default", async () => {
  const ctx = await run(["plan", "in", "out"]);

  const received = await withApi(async (sdk) => { await sdk.saveSnapshotAsync("baseline", ctx); });

  assert.equal(received.length, 1);
  assert.equal(received[0].source_trace_id, ctx.traceId);
  assert.equal(received[0].pinned, true);
});

test("pinned: false saves an unpinned baseline", async () => {
  const received = await withApi(async (sdk) => {
    await sdk.saveSnapshotAsync("baseline", "tr_1", { pinned: false });
  });

  assert.equal(received[0].pinned, false);
});

test("at the pin limit, saving throws with the limit and how to save unpinned", async () => {
  await withApi(async (sdk) => {
    await assert.rejects(sdk.saveSnapshotAsync("baseline", "tr_1"), (err: unknown) => {
      assert.ok(err instanceof VevalRequestError);
      assert.equal(err.status, 409);
      assert.match(err.message, /pinned snapshot limit reached \(3 of 3 on the free plan\)/);
      assert.match(err.message, /pinned: false/);
      return true;
    });
  }, 409, PIN_LIMIT_BODY);
});

test("a save the API rejects throws with its status", async () => {
  await withApi(async (sdk) => {
    await assert.rejects(sdk.saveSnapshotAsync("baseline", "tr_1"), (err: unknown) => {
      assert.ok(err instanceof VevalRequestError);
      assert.equal(err.status, 404);
      assert.match(err.message, /not found/);
      assert.doesNotMatch(err.message, /pinned: false/);
      return true;
    });
  }, 404, `{"error":"trace 'tr_1' not found"}`);
});

test("a network failure throws without a status", async () => {
  // helpers points the SDK at a closed port.
  await assert.rejects(new VevalSdk({ apiKey: "key" }).saveSnapshotAsync("baseline", "tr_1"), (err: unknown) => {
    assert.ok(err instanceof VevalRequestError);
    assert.equal(err.status, null);
    return true;
  });
});
