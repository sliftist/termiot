const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { listCmd, sleep, appLogTail } = require("./procs");

const EXE = path.join(__dirname, "..", "Termiot", "bin", "Debug", "net10.0-windows", "Termiot.exe");
const STATE_DIR = path.join(os.homedir(), "AppData", "Local", "Termiot");
const WATCH_SECONDS = 4;

// Spawns a process via WMI so it escapes the caller's job object entirely (it parents under WmiPrvSE). Used to discriminate "our ConPTY code is broken" from "the environment running this script breaks conhost".
function spawnViaWmi(commandLine) {
    const script = `(Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='${commandLine.replace(/'/g, "''")}'}).ProcessId`;
    const encoded = Buffer.from(script, "utf16le").toString("base64");
    return new Promise((resolve) => {
        const ps = spawn("powershell", ["-NoProfile", "-EncodedCommand", encoded], { stdio: ["ignore", "pipe", "pipe"] });
        let out = "";
        ps.stdout.on("data", (chunk) => (out += chunk));
        ps.stderr.on("data", (chunk) => process.stderr.write(chunk));
        ps.on("error", () => resolve(undefined));
        ps.on("close", () => {
            const pid = parseInt(out.trim(), 10);
            resolve(Number.isFinite(pid) ? pid : undefined);
        });
    });
}

function isAlive(pid) {
    return new Promise((resolve) => {
        const script = `if (Get-Process -Id ${pid} -ErrorAction SilentlyContinue) { 'alive' } else { 'dead' }`;
        const encoded = Buffer.from(script, "utf16le").toString("base64");
        const ps = spawn("powershell", ["-NoProfile", "-EncodedCommand", encoded], { stdio: ["ignore", "pipe", "pipe"] });
        let out = "";
        ps.stdout.on("data", (chunk) => (out += chunk));
        ps.on("error", () => resolve(false));
        ps.on("close", () => resolve(out.includes("alive")));
    });
}

// Runs a shell host directly (no UI involved) and reports whether its cmd.exe stays alive — isolates ConPTY setup problems from UI-driven ones. Pass --wmi to launch the host outside this script's job object.
async function main() {
    const useWmi = process.argv.includes("--wmi");
    const tabId = "hosttest" + Date.now().toString(36);
    let hostPid;
    if (useWmi) {
        hostPid = await spawnViaWmi(`"${EXE}" --shellhost ${tabId} ${os.homedir()}`);
        if (hostPid === undefined) {
            console.log("WMI spawn failed.");
            process.exit(1);
        }
        console.log(`Spawned host via WMI, pid ${hostPid} tab ${tabId}`);
    } else {
        const host = spawn(EXE, ["--shellhost", tabId, os.homedir()], { stdio: "ignore" });
        hostPid = host.pid;
        console.log(`Spawned host pid ${hostPid} tab ${tabId}`);
    }
    for (let i = 1; i <= WATCH_SECONDS; i++) {
        await sleep(1000);
        const hostAlive = await isAlive(hostPid);
        const cmds = (await listCmd()).filter((c) => c.parentPid === hostPid);
        console.log(`t=${i}s: host ${hostAlive ? "alive" : "dead"}, cmd children: ${cmds.map((c) => c.pid).join(", ") || "none"}`);
    }
    const logPath = path.join(STATE_DIR, "shells", tabId, "output.log");
    if (fs.existsSync(logPath)) {
        console.log("--- shell log (escaped) ---");
        console.log(JSON.stringify(fs.readFileSync(logPath).toString("utf8")));
    }
    console.log("--- app.log (last 6) ---");
    console.log(appLogTail(6));
    const { kill } = require("./procs");
    await kill(hostPid);
    await sleep(500);
    try {
        fs.rmSync(path.join(STATE_DIR, "shells", tabId), { recursive: true, force: true });
    } catch (err) {
    }
}

main();
