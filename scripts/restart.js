const { spawn } = require("child_process");
const path = require("path");
const { listTermiot, kill, sleep } = require("./procs");

// Shells self-terminate ~10-15s after their window dies (5s liveness checks + orphan grace); they hold the exe lock until then, so the build has to wait them out.
const SHELL_DRAIN_TIMEOUT_S = 40;

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
    const dest = await require("./build").buildAndStage();
    if (!dest) {
        process.exit(1);
    }
    const child = spawn(path.join(dest, "Termiot.exe"), [], { detached: true, stdio: "ignore" });
    child.unref();
    console.log("Termiot restarted — windows will resurrect from their state files.");
}

main();
