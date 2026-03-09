import fs from "node:fs";
import path from "node:path";

const args = process.argv.slice(2);

function readArg(name, fallback = undefined) {
  const index = args.indexOf(name);
  if (index === -1 || index === args.length - 1) {
    return fallback;
  }
  return args[index + 1];
}

const url = readArg("--url");
const artifactsDir = readArg("--artifacts-dir");
const logsDir = readArg("--logs-dir");
const statusUrl = readArg("--status-url");
const timeoutMs = Number(readArg("--timeout-ms", "1800000"));
const slowMo = Number(readArg("--slowmo-ms", "100"));
const browserPreference = readArg("--browser", "msedge");

if (!url || !artifactsDir || !logsDir || !statusUrl) {
  console.error("Missing required arguments.");
  process.exit(2);
}

fs.mkdirSync(artifactsDir, { recursive: true });
fs.mkdirSync(logsDir, { recursive: true });

const consoleLogPath = path.join(logsDir, "driver-console.jsonl");
const networkLogPath = path.join(logsDir, "driver-network.jsonl");

function appendJsonLine(filePath, payload) {
  fs.appendFileSync(filePath, `${JSON.stringify(payload)}\n`, "utf8");
}

async function tryScreenshot(page, filePath) {
  try {
    await page.screenshot({ path: filePath, fullPage: true });
    return true;
  } catch (error) {
    appendJsonLine(consoleLogPath, {
      ts: new Date().toISOString(),
      type: "driver-warning",
      text: `Screenshot skipped: ${String(error)}`,
      path: filePath,
    });
    return false;
  }
}

const { chromium } = await import("playwright");

function createLaunchOptions() {
  const base = {
    headless: false,
    slowMo,
    args: ["--disable-features=msEdgeSidebarV2", "--disable-background-networking"],
  };

  if (browserPreference === "chrome") {
    return { ...base, channel: "chrome" };
  }

  if (browserPreference === "msedge") {
    return { ...base, channel: "msedge" };
  }

  return base;
}

async function pollStatus() {
  const response = await fetch(statusUrl, { method: "GET" });
  return await response.json();
}

let browser;
let context;
let page;

try {
  browser = await chromium.launch(createLaunchOptions());
  context = await browser.newContext({
    viewport: { width: 1540, height: 980 },
    ignoreHTTPSErrors: true,
  });
  page = await context.newPage();

  page.on("console", async message => {
    appendJsonLine(consoleLogPath, {
      ts: new Date().toISOString(),
      type: message.type(),
      text: message.text(),
    });
  });

  page.on("pageerror", error => {
    appendJsonLine(consoleLogPath, {
      ts: new Date().toISOString(),
      type: "pageerror",
      text: String(error),
    });
  });

  page.on("response", response => {
    appendJsonLine(networkLogPath, {
      ts: new Date().toISOString(),
      url: response.url(),
      status: response.status(),
      ok: response.ok(),
    });
  });

  await page.goto(url, { waitUntil: "domcontentloaded", timeout: 120000 });
  await tryScreenshot(page, path.join(artifactsDir, "checkpoint-00-loaded.png"));

  const deadline = Date.now() + timeoutMs;
  const seenCheckpointNames = new Set();
  let mainShotTaken = false;
  let chaosShotTaken = false;

  while (Date.now() < deadline) {
    const status = await pollStatus();

    if (Array.isArray(status.checkpoints)) {
      for (const checkpoint of status.checkpoints) {
        const name = String(checkpoint.name ?? "checkpoint");
        if (seenCheckpointNames.has(name)) {
          continue;
        }
        seenCheckpointNames.add(name);
        const safe = name.replace(/[^a-z0-9._-]/gi, "_").toLowerCase();
        await tryScreenshot(page, path.join(artifactsDir, `checkpoint-${safe}.png`));
      }
    }

    if (!mainShotTaken && status.reports?.main) {
      await tryScreenshot(page, path.join(artifactsDir, "checkpoint-main-complete.png"));
      mainShotTaken = true;
    }

    if (!chaosShotTaken && status.reports?.chaos) {
      await tryScreenshot(page, path.join(artifactsDir, "checkpoint-chaos-complete.png"));
      chaosShotTaken = true;
    }

    if (status.complete) {
      await tryScreenshot(page, path.join(artifactsDir, "checkpoint-final.png"));
      process.exit(0);
    }

    await page.waitForTimeout(1500);
  }

  await tryScreenshot(page, path.join(artifactsDir, "checkpoint-timeout.png"));
  console.error(`Timed out after ${timeoutMs} ms waiting for scenario completion.`);
  process.exit(1);
} catch (error) {
  appendJsonLine(consoleLogPath, {
    ts: new Date().toISOString(),
    type: "driver-error",
    text: String(error),
  });
  if (page) {
    try {
      await page.screenshot({ path: path.join(artifactsDir, "checkpoint-driver-error.png"), fullPage: true });
    } catch {
      // ignore secondary failure
    }
  }
  console.error(error);
  process.exit(1);
} finally {
  await context?.close().catch(() => {});
  await browser?.close().catch(() => {});
}
