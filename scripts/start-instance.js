const { spawn } = require("child_process");
const os = require("os");
const path = require("path");

const USER_DOTNET_ROOT = path.join(os.homedir(), ".dotnet");

// Build+stage (dated folder, stable bat repointed), then start the staged copy — the running instance holds locks on the copy, never on bin\Debug, so rebuilding is always possible while it runs.
async function main() {
    const dest = await require("./build").buildAndStage();
    if (!dest) {
        process.exit(1);
    }
    const child = spawn(path.join(dest, "Termiot.exe"), [], { detached: true, stdio: "ignore", env: { ...process.env, DOTNET_ROOT: USER_DOTNET_ROOT } });
    child.unref();
    console.log("Instance started.");
}

main();
