// Node 20: node --experimental-websocket .github/scripts/browser_contract.mjs --browser EXE --html FILE --out DIR
// Fixture: <=100 nodes, >=2 node/edge kinds, diagnostics, truncation, distinct
// artifact/candidate/display counts, and a uniquely named node
// with distinct incoming/outgoing neighbors, NFC-decomposable text, CJK/emoji and
// literal </script> text. Use __srcnet_injected=true in an injection payload.
import fs from "node:fs/promises";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { parseArgs } from "node:util";
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { setTimeout as sleep } from "node:timers/promises";

const { values } = parseArgs({ options: {
  browser: { type: "string" }, html: { type: "string" }, out: { type: "string" }
} });
if (!values.browser || !values.html || !values.out || typeof WebSocket !== "function")
  throw new Error("Use node --experimental-websocket browser_contract.mjs --browser EXE --html FILE --out DIR");
const html = path.resolve(values.html), out = path.resolve(values.out);
await fs.access(html);
await fs.access(path.resolve(values.browser));
await fs.mkdir(out, { recursive: true });
const profile = await fs.mkdtemp(path.join(out, "profile-"));
const report = { html, browser: path.resolve(values.browser), checks: [], requests: [], exceptions: [], dialogs: [] };
let child, socket, command, stderr = "", exitPromise;
const interrupt = () => { report.interrupted = true; process.exitCode = 1; if (child) child.kill(); };
process.once("SIGINT", interrupt); process.once("SIGTERM", interrupt);
const pending = new Map();
const json = JSON.stringify;
function check(name, pass) {
  report.checks.push({ name, pass: Boolean(pass) });
  if (!pass) throw new Error("Contract failed: " + name);
}
function record(list, value) {
  if (list.length < 100) list.push(String(value).slice(0, 4096));
  else report.eventOverflow = true;
}
async function until(action, label, timeout = 10000) {
  const end = Date.now() + timeout;
  do { if (await action()) return; await sleep(40); } while (Date.now() < end);
  throw new Error("Timed out: " + label);
}
try {
  if (report.interrupted) throw new Error("Interrupted");
  child = spawn(report.browser, ["--headless=new", "--remote-debugging-port=0",
    "--remote-debugging-address=127.0.0.1", "--user-data-dir=" + profile,
    "--no-first-run", "--no-default-browser-check", "--disable-background-networking",
    "--disable-component-update", "--disable-sync", "--disable-default-apps",
    "--host-resolver-rules=MAP * ~NOTFOUND", "--window-size=1440,1000", "about:blank"],
    { stdio: ["ignore", "ignore", "pipe"] });
  let ended = false, spawnError;
  exitPromise = new Promise(resolve => {
    child.once("exit", () => { ended = true; resolve(); });
    child.once("error", error => { ended = true; spawnError = error; resolve(); });
  });
  child.stderr.on("data", bytes => { stderr = (stderr + bytes).slice(-65536); });
  await until(() => {
    if (spawnError) throw spawnError;
    if (ended) throw new Error("Browser exited before CDP startup");
    return /DevTools listening on ws:/.test(stderr);
  }, "browser startup");
  socket = new WebSocket(stderr.match(/DevTools listening on (ws:\/\/[^\s]+)/)[1]);
  await until(() => {
    if (socket.readyState === WebSocket.CLOSED) throw new Error("CDP connection closed");
    return socket.readyState === WebSocket.OPEN;
  }, "CDP connection");
  let sequence = 0;
  socket.addEventListener("close", () => {
    for (const entry of pending.values()) { clearTimeout(entry.timer); entry.reject(new Error("CDP closed")); }
    pending.clear();
  });
  socket.addEventListener("message", event => {
    const message = JSON.parse(event.data);
    if (message.id) {
      const entry = pending.get(message.id);
      if (!entry) return;
      clearTimeout(entry.timer); pending.delete(message.id);
      if (message.error) entry.reject(new Error(json(message.error)));
      else entry.resolve(message.result);
    } else {
      if (message.method === "Network.requestWillBeSent") record(report.requests, message.params.request.url);
      if (message.method === "Runtime.exceptionThrown") record(report.exceptions, message.params.exceptionDetails.text);
      if (message.method === "Page.javascriptDialogOpening") record(report.dialogs, message.params.type);
    }
  });
  command = (method, params = {}, sessionId) => new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { pending.delete(id); reject(new Error("CDP timeout: " + method)); }, 10000);
    pending.set(id, { resolve, reject, timer });
    socket.send(json({ id, method, params, ...(sessionId ? { sessionId } : {}) }));
  });
  const { targetId } = await command("Target.createTarget", { url: "about:blank" });
  const { sessionId } = await command("Target.attachToTarget", { targetId, flatten: true });
  const send = (method, params) => command(method, params, sessionId);
  const evaluate = async expression => {
    const result = await send("Runtime.evaluate", {
      expression: `{const $=id=>document.getElementById(id),all=s=>[...document.querySelectorAll(s)];${expression}}`,
      returnByValue: true, awaitPromise: true
    });
    if (result.exceptionDetails) throw new Error("Page evaluation: " + result.exceptionDetails.text);
    return result.result.value;
  };
  const settle = () => evaluate("new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))");
  const image = async () => createHash("sha256").update(
    await evaluate('document.querySelector("canvas").toDataURL()')).digest("hex");
  const reset = async () => { await evaluate('$("reset").click()'); await settle(); };
  await send("Page.enable"); await send("Runtime.enable"); await send("Network.enable");
  await send("Page.addScriptToEvaluateOnNewDocument", { source: "globalThis.__srcnet_injected=false;" });
  await send("Network.emulateNetworkConditions", { offline: true, latency: 0, downloadThroughput: 0, uploadThroughput: 0 });
  await send("Emulation.setDeviceMetricsOverride", { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await send("Page.navigate", { url: pathToFileURL(html).href });
  await until(() => evaluate('document.readyState==="complete" && !!$("srcnet-data")'), "page load");
  await settle();
  const data = await evaluate('JSON.parse($("srcnet-data").textContent)');
  const reference = data.nodes.findIndex((n, i) =>
    n.qualifiedName.normalize("NFD") !== n.qualifiedName.normalize("NFC") &&
    [n.name, n.qualifiedName, n.path].join(" ").includes("</script>") &&
    /\p{Script=Han}/u.test([n.name, n.qualifiedName, n.path].join(" ")) &&
    /\p{Extended_Pictographic}/u.test([n.name, n.qualifiedName, n.path].join(" ")) &&
    data.nodes.filter(other => other.qualifiedName === n.qualifiedName).length === 1 &&
    data.edges.some(e => e.to === i && e.from !== i) && data.edges.some(e => e.from === i && e.to !== i));
  check("fixture coverage", data.schemaVersion === 1 && data.nodes.length <= 100 && reference >= 0 &&
    data.diagnostics.length > 0 && new Set(data.nodes.map(n => n.kind)).size >= 2 &&
    new Set(data.edges.map(e => e.kind)).size >= 2 &&
    data.truncated && data.totalNodes > data.candidateNodes && data.candidateNodes > data.nodes.length);
  const node = data.nodes[reference];
  const select = async () => {
    await evaluate(`(() => {const input=$("filter");input.value=${json(node.qualifiedName.normalize("NFD"))};
      input.dispatchEvent(new Event("input",{bubbles:true}));
      const hit=all("#hits button").find(b=>b.textContent.includes(${json(node.qualifiedName)}));
      if(!hit)throw Error("Missing NFC search result");hit.click();})()`);
    await settle();
    check("search selection details", await evaluate(`${json([node.id, node.name, node.qualifiedName, node.path])}
      .every(v=>$("selection").textContent.includes(v)) && !$("direction").disabled`));
  };
  const lists = async (direction, excludedKind = "") => {
    const edges = data.edges.filter(e => e.kind !== excludedKind &&
      ((direction !== "in" && e.from === reference) || (direction !== "out" && e.to === reference)));
    const expected = [
      ...edges.filter(e => e.from === reference).slice(0, 100).map(e => [e.kind, data.nodes[e.to].qualifiedName]),
      ...edges.filter(e => e.to === reference).slice(0, 100).map(e => [e.kind, data.nodes[e.from].qualifiedName])
    ].map(([kind, name]) => kind + name).sort();
    const actual = await evaluate('all("#selection button").map(b=>b.textContent).sort()');
    check("neighbor lists " + direction + " " + excludedKind, json(actual) === json(expected));
  };
  await reset();
  const baseline = await image();
  check("unselected direction disabled", await evaluate('$("direction").disabled && !!$("directionReference").textContent'));
  await select();
  const directions = [];
  for (const value of ["in", "out", "both"]) {
    await evaluate(`$("direction").value=${json(value)};$("direction").dispatchEvent(new Event("change"))`);
    await settle(); await lists(value); directions.push(await image());
  }
  check("direction changes canvas", directions[0] !== directions[1]);
  const toggle = (container, kind) => evaluate(`(() => {const box=all(${json(container + " input")})
    .find(b=>b.closest("label").textContent.trim()===${json(kind)});if(!box)throw Error("Missing kind toggle");box.click();})()`);
  const edgeKind = data.edges.find(e => e.from === reference || e.to === reference).kind;
  await toggle("#edgeKinds", edgeKind); await settle(); await lists("both", edgeKind);
  check("edge filter changes canvas", await image() !== directions[2]);
  await toggle("#edgeKinds", edgeKind); await settle();
  check("edge filter restores canvas", await image() === directions[2]);
  await toggle("#kinds", node.kind); await settle();
  check("hidden selection cleared", await evaluate('$("direction").disabled && all("#selection button").length===0'));
  check("node filter changes canvas", await image() !== directions[2]);
  await toggle("#kinds", node.kind); await select(); await lists("both");
  const visible = await evaluate(`(() => {document.querySelector("aside").scrollTop=100000;
    const inside=id=>{const r=$(id).getBoundingClientRect();return r.height>0&&r.top>=0&&r.bottom<=innerHeight;};
    return ["counts","diagnostics","truncation"].every(inside)&&document.documentElement.scrollHeight===innerHeight &&
      !!$("truncation").textContent &&
      ${json([String(data.totalNodes), String(data.candidateNodes)])}.every(n=>$("counts").textContent.includes(n)) &&
      ${json(data.diagnostics)}.every(d=>$("diagnostics").textContent.includes(d));})()`);
  check("totals and diagnostics remain visible", visible);
  await reset();
  const point = await evaluate('(()=>{const r=document.querySelector("canvas").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()');
  await send("Input.dispatchMouseEvent", { type: "mouseWheel", ...point, deltaX: 0, deltaY: -200 });
  await until(async () => await image() !== baseline, "zoom changes canvas");
  const zoomed = await image();
  check("zoom changes canvas", zoomed !== baseline);
  await send("Input.dispatchMouseEvent", { type: "mousePressed", ...point, button: "left", buttons: 1, clickCount: 1 });
  await send("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x + 40, y: point.y + 30, buttons: 1 });
  await send("Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x + 40, y: point.y + 30, button: "left", buttons: 0, clickCount: 1 });
  await settle(); check("pan changes canvas", await image() !== zoomed);
  await reset(); check("reset restores canvas", await image() === baseline);
  check("offline without exceptions or injection", report.requests.length > 0 &&
    report.requests.every(url => url.startsWith("file:") || url.startsWith("data:")) &&
    !report.exceptions.length && !report.dialogs.length && !report.eventOverflow &&
    await evaluate('!globalThis.__srcnet_injected && document.scripts.length===2'));
  const screenshot = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
  await fs.writeFile(path.join(out, "contract.png"), Buffer.from(screenshot.data, "base64"));
} catch (error) {
  report.error = String(error.stack ?? error); process.exitCode = 1;
} finally {
  try {
    if (command && socket.readyState === WebSocket.OPEN) await command("Browser.close");
  } catch (error) { report.cleanupError = String(error); process.exitCode = 1; }
  finally {
    if (socket) socket.close();
    if (child && child.exitCode === null && child.signalCode === null) child.kill();
    if (exitPromise) {
      await Promise.race([exitPromise, sleep(3000, undefined, { ref: false })]);
      if (child.exitCode === null && child.signalCode === null && child.pid) {
        child.kill("SIGKILL");
        await Promise.race([exitPromise, sleep(3000, undefined, { ref: false })]);
        if (child.exitCode === null && child.signalCode === null) {
          report.cleanupError = "Browser did not exit"; process.exitCode = 1;
        }
      }
    }
    for (const entry of pending.values()) clearTimeout(entry.timer);
    try { await fs.rm(profile, { recursive: true, maxRetries: 10, retryDelay: 100 }); }
    catch (error) { report.cleanupError = String(error); process.exitCode = 1; }
    await fs.writeFile(path.join(out, "results.json"), json(report, null, 2));
    await fs.writeFile(path.join(out, "browser.log"), stderr);
    process.removeListener("SIGINT", interrupt); process.removeListener("SIGTERM", interrupt);
  }
}
console.log(json({ out, passed: report.checks.filter(c => c.pass).length, error: report.error, cleanupError: report.cleanupError }));
