const fs = require("fs");
const os = require("os");
const path = require("path");

// The one path that never changes: every external registration (Cursor, Explorer, Start menu, startup entry) points here, and every build repoints it at the newest exe — so registrations never go stale.
const BAT = path.join(os.homedir(), "AppData", "Local", "Termiot", "termiot.bat");

function writeStableBat(exePath) {
    fs.mkdirSync(path.dirname(BAT), { recursive: true });
    fs.writeFileSync(BAT, `@echo off\r\nstart "" "${exePath}" %*\r\n`);
    console.log(`Stable launcher -> ${exePath}`);
}

module.exports = { writeStableBat, BAT };
