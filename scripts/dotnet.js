const { spawn } = require("child_process");
const fs = require("fs");
const os = require("os");
const path = require("path");

// The SDK was installed user-scoped (via dotnet-install.ps1, no admin) rather than machine-wide, so it isn't on PATH and the built apphost can't find the runtime without DOTNET_ROOT.
const USER_DOTNET_ROOT = path.join(os.homedir(), ".dotnet");

function resolveDotnet() {
    const userDotnet = path.join(USER_DOTNET_ROOT, "dotnet.exe");
    if (fs.existsSync(userDotnet)) {
        return { exe: userDotnet, root: USER_DOTNET_ROOT };
    }
    return { exe: "dotnet", root: undefined };
}

function main() {
    const args = process.argv.slice(2);
    const { exe, root } = resolveDotnet();
    const env = { ...process.env };
    if (root !== undefined) {
        env.DOTNET_ROOT = root;
        env.PATH = `${root}${path.delimiter}${env.PATH}`;
    }
    const child = spawn(exe, args, { env, stdio: ["inherit", "pipe", "pipe"] });
    child.stdout.on("data", (chunk) => process.stdout.write(chunk));
    child.stderr.on("data", (chunk) => process.stderr.write(chunk));
    child.on("error", (err) => {
        console.error(err.stack ?? err);
        process.exit(1);
    });
    child.on("close", (code) => process.exit(code ?? 1));
}

main();
