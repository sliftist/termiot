const fs = require("fs");
const os = require("os");
const path = require("path");
const { listTermiot, listCmd, appLogTail } = require("./procs");

const STATE_DIR = path.join(os.homedir(), "AppData", "Local", "Termiot");
const SHELL_LOG_TAIL_BYTES = 400;

async function main() {
    const procs = await listTermiot();
    if (procs.length === 0) {
        console.log("No Termiot processes running.");
    }
    for (const p of procs) {
        console.log(`${p.pid} [${p.kind}] ${p.commandLine}`);
    }
    const hostPids = new Set(procs.filter((p) => p.kind === "host").map((p) => p.pid));
    const cmds = (await listCmd()).filter((c) => hostPids.has(c.parentPid));
    for (const c of cmds) {
        console.log(`${c.pid} [cmd] child of host ${c.parentPid}`);
    }
    console.log("--- app.log (last 10) ---");
    console.log(appLogTail(10));
    const shellsDir = path.join(STATE_DIR, "shells");
    if (fs.existsSync(shellsDir)) {
        for (const id of fs.readdirSync(shellsDir)) {
            const logPath = path.join(shellsDir, id, "output.log");
            if (!fs.existsSync(logPath)) {
                console.log(`--- shells/${id}: no output.log ---`);
                continue;
            }
            const buf = fs.readFileSync(logPath);
            const tail = buf.slice(Math.max(0, buf.length - SHELL_LOG_TAIL_BYTES));
            console.log(`--- shells/${id}/output.log (${buf.length} bytes, last ${tail.length} shown, escaped) ---`);
            console.log(JSON.stringify(tail.toString("utf8")));
        }
    }
}

main();
