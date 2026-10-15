// A trace or scenario run that can't be delivered is logged by default and thrown in strict mode.
// Mirrors sdk/python/tests/test_send_failures.py.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { VevalExecutionContext, VevalSdk, VevalSendError, VevalTestSdk } from "../src";

// The endpoint is read when the SDK is constructed.
async function withEndpoint<T>(url: string, body: () => Promise<T>): Promise<T> {
  const previous = process.env.VEVAL_INTERNAL_ENDPOINT;
  process.env.VEVAL_INTERNAL_ENDPOINT = url;
  try {
    return await body();
  } finally {
    if (previous === undefined) delete process.env.VEVAL_INTERNAL_ENDPOINT;
    else process.env.VEVAL_INTERNAL_ENDPOINT = previous;
  }
}

// Rejects requests whose path starts with one of `rejected`; accepts everything else.
async function withUnauthorizedApi<T>(body: () => Promise<T>, rejected = ["/"]): Promise<T> {
  const server = createServer((req, res) => {
    req.resume();
    req.on("end", () => {
      if (!rejected.some((prefix) => req.url!.startsWith(prefix))) {
        res.writeHead(200).end();
        return;
      }
      res.writeHead(401, { "Content-Type": "application/json" });
      res.end('{"error":"invalid api key"}');
    });
  });
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));
  try {
    return await withEndpoint(`http://127.0.0.1:${(server.address() as AddressInfo).port}`, body);
  } finally {
    server.closeAllConnections();
    await new Promise((resolve) => server.close(resolve));
  }
}

async function capturingWarnings(body: () => Promise<void>): Promise<string[]> {
  const logged: string[] = [];
  const original = console.warn;
  console.warn = (message: unknown) => { logged.push(String(message)); };
  try {
    await body();
  } finally {
    console.warn = original;
  }
  return logged;
}

const answer = async (_ctx: VevalExecutionContext) => "answer";
const crash = async (_ctx: VevalExecutionContext): Promise<string> => { throw new Error("agent crashed"); };

test("a rejected trace is logged with its status and the run still succeeds", async () => {
  const logged = await capturingWarnings(() => withUnauthorizedApi(async () => {
    assert.equal(await new VevalSdk({ apiKey: "wrong" }).runAsync("agent", answer), "answer");
  }));

  assert.equal(logged.length, 1);
  assert.match(logged[0], /401/);
  assert.match(logged[0], /invalid api key/);
});

test("strict mode throws a rejected trace", async () => {
  await capturingWarnings(() => withUnauthorizedApi(async () => {
    const sdk = new VevalSdk({ apiKey: "wrong", throwOnSendFailure: true });
    await assert.rejects(sdk.runAsync("agent", answer), (err) => err instanceof VevalSendError && err.status === 401);
  }));
});

test("strict mode throws a network failure", async () => {
  // A port that was just free; not port 1, which fetch rejects as a "bad port" before connecting.
  const probe = createServer();
  await new Promise<void>((resolve) => probe.listen(0, "127.0.0.1", resolve));
  const closedPort = (probe.address() as AddressInfo).port;
  await new Promise((resolve) => probe.close(resolve));

  const logged = await capturingWarnings(() => withEndpoint(`http://127.0.0.1:${closedPort}`, async () => {
    const sdk = new VevalSdk({ apiKey: "k", throwOnSendFailure: true });
    await assert.rejects(sdk.runAsync("agent", answer), (err) => err instanceof VevalSendError && err.status === null);
  }));
  assert.equal(logged.length, 1);
  assert.match(logged[0], /failed: \S/, "the warning names the reason");
});

test("strict mode keeps the agent's own error", async () => {
  for (const Sdk of [VevalSdk, VevalTestSdk]) {
    const logged = await capturingWarnings(() => withUnauthorizedApi(async () => {
      const sdk = new Sdk({ apiKey: "wrong", throwOnSendFailure: true });
      await assert.rejects(sdk.runAsync("agent", crash), /agent crashed/);
    }));
    assert.ok(logged.some((m) => m.includes("401")));
  }
});

test("strict mode throws a rejected scenario run", async () => {
  const logged = await capturingWarnings(() => withUnauthorizedApi(async () => {
    const sdk = new VevalTestSdk({ apiKey: "wrong", throwOnSendFailure: true });
    await assert.rejects(
      sdk.runScenarioAsync("echo", answer, [], [{ name: "a", input: "hi", assertions: [] }]),
      (err) => err instanceof VevalSendError && err.status === 401
    );
  }));
  assert.ok(logged.some((m) => m.includes("scenario run 'echo'")));
});

test("strict mode runs every scenario item before throwing", async () => {
  const calls: unknown[] = [];
  const counting = async (ctx: VevalExecutionContext) => { calls.push(ctx.input); return "answer"; };

  const logged = await capturingWarnings(() => withUnauthorizedApi(async () => {
    const sdk = new VevalSdk({ apiKey: "wrong", throwOnSendFailure: true });
    const items = ["a", "b", "c"].map((n) => ({ name: n, input: n, assertions: [] }));
    await assert.rejects(sdk.runScenarioAsync("echo", counting, [], items), VevalSendError);
  }));

  assert.deepEqual(calls, ["a", "b", "c"]);
  assert.equal(logged.filter((m) => m.includes("Sending trace")).length, 3);
  assert.ok(logged.some((m) => m.includes("scenario run 'echo'")));
});

test("strict mode throws after a posted scenario whose traces were lost", async () => {
  await capturingWarnings(() => withUnauthorizedApi(async () => {
    const sdk = new VevalSdk({ apiKey: "k", throwOnSendFailure: true });
    await assert.rejects(
      sdk.runScenarioAsync("echo", answer, [], [{ name: "a", input: "hi", assertions: [] }]),
      /1 trace\(s\) from scenario 'echo'/
    );
  }, ["/v1/traces"]));
});
