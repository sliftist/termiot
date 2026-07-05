const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");
const { appLogTail, sleep } = require("./procs");

// Builds to the isolated claude-check folder and runs the binary's --selftest, which exercises the full production path (session → spawned host → ConPTY cmd.exe → pipe → parser → screen). Never touches bin\Debug or any running instance.
const EXE = path.join(__dirname, "..", "Termiot", "bin", "claude-check", "Termiot.exe");
const RESULT = path.join(os.homedir(), "AppData", "Local", "Termiot", "test-result.txt");
const USER_DOTNET_ROOT = path.join(os.homedir(), ".dotnet");
const SELFTEST_TIMEOUT_MS = 30000;

function run(exe, args, extraEnv) {
    return new Promise((resolve) => {
        const child = spawn(exe, args, { stdio: ["ignore", "pipe", "pipe"], env: { ...process.env, ...extraEnv } });
        child.stdout.on("data", (chunk) => process.stdout.write(chunk));
        child.stderr.on("data", (chunk) => process.stderr.write(chunk));
        child.on("error", (err) => {
            console.error(err.stack ?? err);
            resolve(1);
        });
        const timer = setTimeout(() => {
            console.log("selftest timed out — killing");
            child.kill();
        }, SELFTEST_TIMEOUT_MS);
        child.on("close", (code) => {
            clearTimeout(timer);
            resolve(code ?? 1);
        });
    });
}

async function main() {
    const buildCode = await run("node", [path.join(__dirname, "dotnet.js"), "build", "Termiot/Termiot.csproj", "-o", "Termiot/bin/claude-check"]);
    if (buildCode !== 0) {
        process.exit(buildCode);
    }
    try {
        fs.unlinkSync(RESULT);
    } catch (err) {
    }
    const code = await run(EXE, ["--selftest"], { DOTNET_ROOT: USER_DOTNET_ROOT });
    await sleep(300);
    console.log(`--- selftest exit code: ${code} ---`);
    if (fs.existsSync(RESULT)) {
        console.log(fs.readFileSync(RESULT, "utf8"));
    } else {
        console.log("(no result file written)");
    }
    console.log("--- app.log (last 8) ---");
    console.log(appLogTail(8));
    process.exit(code);
}

main();
