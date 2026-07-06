const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const SRC = path.join(__dirname, "..", "Termiot", "bin", "Debug", "net10.0-windows");
const INSTANCES = path.join(__dirname, "..", "Termiot", "bin", "instances");
const APP_LOG = path.join(os.homedir(), "AppData", "Local", "Termiot", "app.log");
const USER_DOTNET_ROOT = path.join(os.homedir(), ".dotnet");

// Runs headless (spawned by the renderer's reload button), so results go to the app log where the user can see them.
function log(message) {
    const line = `${new Date().toISOString().replace("T", " ").slice(0, 23)} [${process.pid}] [reload] ${message}\n`;
    try {
        fs.appendFileSync(APP_LOG, line);
    } catch (err) {
    }
    console.log(message);
}

function run(exe, args) {
    return new Promise((resolve) => {
        const child = spawn(exe, args, { stdio: ["ignore", "pipe", "pipe"] });
        let output = "";
        child.stdout.on("data", (chunk) => (output += chunk));
        child.stderr.on("data", (chunk) => (output += chunk));
        child.on("error", (err) => resolve({ code: 1, output: String(err.stack ?? err) }));
        child.on("close", (code) => resolve({ code: code ?? 1, output }));
    });
}

function timestamp() {
    const now = new Date();
    const pad = (n) => String(n).padStart(2, "0");
    return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}_${pad(now.getHours())}-${pad(now.getMinutes())}-${pad(now.getSeconds())}`;
}

// Rebuild → copy to a fresh instance folder → start a takeover process for the given window. The old window is only killed by the new process AFTER a successful build, so failures cost nothing.
async function main() {
    const windowId = process.argv[2];
    if (!windowId) {
        log("no window id given");
        process.exit(1);
    }
    log(`rebuilding for window ${windowId}...`);
    const build = await run("node", [path.join(__dirname, "dotnet.js"), "build", "Termiot"]);
    if (build.code !== 0) {
        log("BUILD FAILED:\n" + build.output.split("\n").filter((l) => l.includes("error") || l.includes("Error")).join("\n"));
        process.exit(build.code);
    }
    const dest = path.join(INSTANCES, timestamp());
    fs.cpSync(SRC, dest, { recursive: true });
    log(`build ok, copied to ${dest}, taking over window ${windowId}`);
    const child = spawn(path.join(dest, "Termiot.exe"), ["--window", windowId, "--resume", "--takeover"], { detached: true, stdio: "ignore", env: { ...process.env, DOTNET_ROOT: USER_DOTNET_ROOT } });
    child.unref();
}

main();
