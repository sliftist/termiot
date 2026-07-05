const net = require("net");
const os = require("os");
const path = require("path");
const fs = require("fs");
const { sleep, appLogTail } = require("./procs");
const { spawn } = require("child_process");

const EXE = path.join(__dirname, "..", "Termiot", "bin", "Debug", "net10.0-windows", "Termiot.exe");
const STATE_DIR = path.join(os.homedir(), "AppData", "Local", "Termiot");

const MSG_HELLO = 1;
const MSG_INPUT = 2;
const MSG_OUTPUT = 5;

function frame(type, payload) {
    const header = Buffer.alloc(5);
    header.writeUInt8(type, 0);
    header.writeInt32LE(payload.length, 1);
    return Buffer.concat([header, payload]);
}

function spawnViaWmi(commandLine) {
    const script = `(Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='${commandLine.replace(/'/g, "''")}'}).ProcessId`;
    const encoded = Buffer.from(script, "utf16le").toString("base64");
    return new Promise((resolve) => {
        const ps = spawn("powershell", ["-NoProfile", "-EncodedCommand", encoded], { stdio: ["ignore", "pipe", "ignore"] });
        let out = "";
        ps.stdout.on("data", (chunk) => (out += chunk));
        ps.on("error", () => resolve(undefined));
        ps.on("close", () => {
            const pid = parseInt(out.replace(/#<[^]*?>/g, "").trim().split(/\s+/).pop(), 10);
            resolve(Number.isFinite(pid) ? pid : undefined);
        });
    });
}

// Acts as a fake renderer against a throwaway shell host: connects, replays, sends a command, and reports every output frame — validates the full host→client live path without any UI.
async function main() {
    const shellId = "pipetest" + Date.now().toString(36);
    const hostPid = await spawnViaWmi(`"${EXE}" --shellhost ${shellId} ${os.homedir()}`);
    console.log(`Host pid ${hostPid} shell ${shellId}`);
    await sleep(1500);

    const sock = net.connect(`\\\\.\\pipe\\termiot-shell-${shellId}`);
    let received = Buffer.alloc(0);
    let outputBytes = 0;
    sock.on("data", (chunk) => {
        received = Buffer.concat([received, chunk]);
        while (received.length >= 5) {
            const type = received.readUInt8(0);
            const len = received.readInt32LE(1);
            if (received.length < 5 + len) {
                break;
            }
            const payload = received.slice(5, 5 + len);
            received = received.slice(5 + len);
            if (type === MSG_OUTPUT) {
                outputBytes += payload.length;
                console.log(`OUTPUT frame ${payload.length} bytes: ${JSON.stringify(payload.toString("utf8").slice(0, 120))}`);
            } else {
                console.log(`frame type ${type} (${len} bytes)`);
            }
        }
    });
    sock.on("error", (err) => console.log("pipe error: " + err.message));

    await new Promise((resolve) => sock.on("connect", resolve));
    console.log("connected — sending Hello(0)");
    const hello = Buffer.alloc(8);
    sock.write(frame(MSG_HELLO, hello));
    await sleep(2000);
    console.log(`--- after replay: ${outputBytes} output bytes total; sending "echo pipetest-works" ---`);
    sock.write(frame(MSG_INPUT, Buffer.from("echo pipetest-works\r", "utf8")));
    await sleep(2000);
    console.log(`--- done: ${outputBytes} output bytes total ---`);
    sock.destroy();
    const { kill } = require("./procs");
    if (hostPid !== undefined) {
        await kill(hostPid);
    }
    await sleep(300);
    try {
        fs.rmSync(path.join(STATE_DIR, "shells", shellId), { recursive: true, force: true });
    } catch (err) {
    }
    console.log("--- app.log (last 4) ---");
    console.log(appLogTail(4));
}

main();
