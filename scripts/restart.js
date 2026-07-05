const { spawn } = require("child_process");
const path = require("path");
const { listTermiot, kill, sleep } = require("./procs");

const EXE = path.join(__dirname, "..", "Termiot", "bin", "Debug", "net10.0-windows", "Termiot.exe");
// Shells self-terminate ~10-15s after their window dies (5s liveness checks + orphan grace); they hold the exe lock until then, so the build has to wait them out.
const SHELL_DRAIN_TIMEOUT_S = 40;

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

// Forcefully kill ONLY the renderer (window) processes — no orderly shutdown, so window state files survive and every window resurrects on relaunch. Shell hosts are left alone: they notice their window died and terminate themselves, proving the no-orphans path.
async function main() {
    const procs = await listTermiot();
    const uis = procs.filter((p) => p.kind === "ui");
    if (uis.length === 0) {
        console.log("No renderer processes running.");
    }
    for (const ui of uis) {
        console.log(`Force-killing renderer ${ui.pid}`);
        await kill(ui.pid);
    }
    for (let i = 0; i < SHELL_DRAIN_TIMEOUT_S; i++) {
        const remaining = await listTermiot();
        if (remaining.length === 0) {
            break;
        }
        if (i === 0 || i % 5 === 0) {
            console.log(`Waiting for ${remaining.length} shell process(es) to self-terminate...`);
        }
        await sleep(1000);
    }
    const leftovers = await listTermiot();
    if (leftovers.length > 0) {
        console.log(`WARNING: ${leftovers.length} Termiot process(es) still alive after ${SHELL_DRAIN_TIMEOUT_S}s — build may fail on locked files.`);
    }
    const buildCode = await run("node", [path.join(__dirname, "dotnet.js"), "build", "Termiot"]);
    if (buildCode !== 0) {
        process.exit(buildCode);
    }
    const child = spawn(EXE, [], { detached: true, stdio: "ignore" });
    child.unref();
    console.log("Termiot restarted — windows will resurrect from their state files.");
}

main();
