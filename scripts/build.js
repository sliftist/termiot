const { spawn } = require("child_process");
const fs = require("fs");
const path = require("path");
const { writeStableBat } = require("./stable-launcher");

const SRC = path.join(__dirname, "..", "Termiot", "bin", "Debug", "net10.0-windows");
const INSTANCES = path.join(__dirname, "..", "Termiot", "bin", "instances");

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

// Every build stages into a fresh dated folder and repoints the stable bat at it: running instances never lock the build output, and everything registered against the bat is on the newest build the moment the build finishes.
async function buildAndStage() {
    const code = await run("node", [path.join(__dirname, "dotnet.js"), "build", "Termiot"]);
    if (code !== 0) {
        return null;
    }
    const dest = path.join(INSTANCES, timestamp());
    fs.cpSync(SRC, dest, { recursive: true });
    writeStableBat(path.join(dest, "Termiot.exe"));
    console.log(`Staged build at ${dest}`);
    return dest;
}

module.exports = { buildAndStage };

if (require.main === module) {
    buildAndStage().then((dest) => process.exit(dest ? 0 : 1));
}
