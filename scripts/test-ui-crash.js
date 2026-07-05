const { listTermiot, kill, sleep, appLogTail } = require("./procs");

const WATCH_SECONDS = 8;

// Simulates an orchestrator (UI) crash and watches whether a shell host respawns it.
async function main() {
    const procs = await listTermiot();
    const ui = procs.find((p) => p.kind === "ui");
    if (!ui) {
        console.log("No UI process running — start the app first (yarn restart).");
        process.exit(1);
    }
    console.log(`Killing UI pid ${ui.pid} (hosts: ${procs.filter((p) => p.kind === "host").map((p) => p.pid).join(", ") || "none"})`);
    await kill(ui.pid);
    for (let i = 1; i <= WATCH_SECONDS; i++) {
        await sleep(1000);
        const now = await listTermiot();
        console.log(`t=${i}s: ${now.map((p) => `${p.pid}(${p.kind})`).join(", ") || "none"}`);
    }
    console.log("--- app.log (last 10) ---");
    console.log(appLogTail(10));
}

main();
