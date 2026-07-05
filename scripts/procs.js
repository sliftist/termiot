const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

const APP_LOG = path.join(os.homedir(), "AppData", "Local", "Termiot", "app.log");

// Returns [{pid, kind: "ui" | "host", commandLine}] for all running Termiot processes. The script is passed base64-encoded because embedded quotes do not survive node → powershell argument parsing reliably.
function listTermiot() {
    const script = "Get-CimInstance Win32_Process -Filter \"Name='Termiot.exe'\" | ForEach-Object { \"$($_.ProcessId)|$($_.CommandLine)\" }";
    const encoded = Buffer.from(script, "utf16le").toString("base64");
    return new Promise((resolve) => {
        const ps = spawn("powershell", ["-NoProfile", "-EncodedCommand", encoded], { stdio: ["ignore", "pipe", "pipe"] });
        let out = "";
        ps.stdout.on("data", (chunk) => (out += chunk));
        ps.on("error", () => resolve([]));
        ps.on("close", () => {
            const procs = [];
            for (const line of out.split(/\r?\n/)) {
                const sep = line.indexOf("|");
                if (sep <= 0) {
                    continue;
                }
                const pid = parseInt(line.slice(0, sep), 10);
                const commandLine = line.slice(sep + 1);
                if (Number.isFinite(pid)) {
                    procs.push({ pid, kind: commandLine.includes("--shellhost") ? "host" : "ui", commandLine });
                }
            }
            resolve(procs);
        });
    });
}

function kill(pid) {
    return new Promise((resolve) => {
        const child = spawn("taskkill", ["/PID", String(pid), "/F"], { stdio: ["ignore", "pipe", "pipe"] });
        child.on("error", () => resolve());
        child.on("close", () => resolve());
    });
}

function sleep(ms) {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

function appLogTail(lines) {
    try {
        const content = fs.readFileSync(APP_LOG, "utf8").trimEnd().split(/\r?\n/);
        return content.slice(-lines).join("\n");
    } catch (err) {
        return "(no app.log)";
    }
}

// Returns [{pid, parentPid}] of all running cmd.exe processes.
function listCmd() {
    const script = "Get-CimInstance Win32_Process -Filter \"Name='cmd.exe'\" | ForEach-Object { \"$($_.ProcessId)|$($_.ParentProcessId)\" }";
    const encoded = Buffer.from(script, "utf16le").toString("base64");
    return new Promise((resolve) => {
        const ps = spawn("powershell", ["-NoProfile", "-EncodedCommand", encoded], { stdio: ["ignore", "pipe", "pipe"] });
        let out = "";
        ps.stdout.on("data", (chunk) => (out += chunk));
        ps.on("error", () => resolve([]));
        ps.on("close", () => {
            const procs = [];
            for (const line of out.split(/\r?\n/)) {
                const [pid, parentPid] = line.split("|").map((s) => parseInt(s, 10));
                if (Number.isFinite(pid)) {
                    procs.push({ pid, parentPid });
                }
            }
            resolve(procs);
        });
    });
}

module.exports = { listTermiot, listCmd, kill, sleep, appLogTail };
