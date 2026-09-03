// One-shot stdio MCP client: send framed requests, print responses.
const exe = process.argv[2];
const args = process.argv.slice(3);
const { spawn } = require("child_process");
const readline = require("readline");

const child = spawn(exe, args, { stdio: ["pipe", "pipe", "pipe"] });
child.stderr.on("data", (d) => process.stderr.write("[stderr] " + d));

const rl = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;

rl.on("line", (line) => {
  if (!line.trim()) return;
  try {
    const msg = JSON.parse(line);
    if (msg.id !== undefined && pending.has(msg.id)) {
      pending.get(msg.id)(msg);
      pending.delete(msg.id);
    }
  } catch (e) {
    process.stderr.write("[bad json] " + line.slice(0, 200) + "\n");
  }
});

function call(method, params, timeoutMs = 120000) {
  return new Promise((resolve) => {
    const id = nextId++;
    const timer = setTimeout(() => {
      pending.delete(id);
      resolve({ timeout: true, method });
    }, timeoutMs);
    pending.set(id, (msg) => {
      clearTimeout(timer);
      resolve(msg);
    });
    child.stdin.write(
      JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n",
    );
  });
}

(async () => {
  console.log(
    JSON.stringify(
      await call("initialize", {
        protocolVersion: "2025-06-18",
        capabilities: {},
        clientInfo: { name: "probe", version: "0" },
      }),
    ),
  );
  child.stdin.write(
    JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) +
      "\n",
  );

  let steps;
  try {
    steps = JSON.parse(process.env.STEPS || "[]");
  } catch {
    steps = [];
  }
  for (const step of steps) {
    let r;
    try {
      r = await call(
        "tools/call",
        { name: step.tool, arguments: step.args || {} },
        step.timeoutMs || 120000,
      );
    } catch (e) {
      console.log("### " + step.label + " FAILED: " + e.message);
      continue;
    }
    const text =
      r?.result?.content?.map((c) => c.text).join("\n") ??
      JSON.stringify(r).slice(0, 3000);
    console.log(
      "### " +
        step.label +
        "\n" +
        (step.grep
          ? text
              .split("\n")
              .filter((l) => step.grep.some((g) => l.toLowerCase().includes(g)))
              .join("\n")
          : text.slice(0, step.head || 3000)),
    );
  }
  child.kill();
  process.exit(0);
})();
