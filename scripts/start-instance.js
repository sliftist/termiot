const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const SRC = path.join(__dirname, "..", "Termiot", "bin", "Debug", "net10.0-windows");
const INSTANCES = path.join(__dirname, "..", "Termiot", "bin", "instances");
const USER_DOTNET_ROOT = path.join(os.homedir(), ".dotnet");

function run(exe, args) {
    return new Promise((resolve) => {
        const child = spawn(exe, args, { stdio: ["inherit", "pipe", "pipe"] });
        child.stdout.on("data", (chunk) => process.stdout.write(chunk));
        child.stderr.on("data", (chunk) => process.stderr.write(chunk));
        child.on("error", (err) => {
            console.error(err.stack ?? err);
            resolve(1);
        });
        child.on("close", (code) => resolve(code ?? 1));
    });
}

function timestamp() {
    const now = new Date();
    const pad = (n) => String(n).padStart(2, "0");
    return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}_${pad(now.getHours())}-${pad(now.getMinutes())}-${pad(now.getSeconds())}`;
}

// Build, copy the output into a timestamped instance folder, and start THAT copy — the running instance holds locks on the copy, never on bin\Debug, so rebuilding is always possible while it runs.
async function main() {
    const buildCode = await run("node", [path.join(__dirname, "dotnet.js"), "build", "Termiot"]);
    if (buildCode !== 0) {
        process.exit(buildCode);
    }
    const dest = path.join(INSTANCES, timestamp());
    fs.cpSync(SRC, dest, { recursive: true });
    console.log(`Copied build to ${dest}`);
    const child = spawn(path.join(dest, "Termiot.exe"), [], { detached: true, stdio: "ignore", env: { ...process.env, DOTNET_ROOT: USER_DOTNET_ROOT } });
    child.unref();
    console.log("Instance started.");
}

main();
