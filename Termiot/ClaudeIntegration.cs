using System.IO;
using System.Text.Json.Nodes;

namespace Termiot;

// Claude Code auto-resume: a SessionStart hook that writes `claude -r <sessionId>` into the Termiot shell's AUTORESUME.cmd (via the SHELLFOLDER env var each shell host sets), so a resumed/restored tab reattaches the exact Claude session that was running in it.
public static class ClaudeIntegration
{
    private const string HookFileName = "write-resume-command.js";

    private static string ClaudeDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    public static string SettingsPath => Path.Combine(ClaudeDir, "settings.json");

    public static string HookPath => Path.Combine(ClaudeDir, "hooks", HookFileName);

    private const string HookScript =
        """
        // Termiot auto-resume hook: on Claude Code session start, records `claude -r <sessionId>` into the surrounding Termiot shell's AUTORESUME.cmd (SHELLFOLDER is set by the Termiot shell host), so restoring that tab resumes this session.
        const fs = require("fs");
        const path = require("path");

        const RESUME_DIR_ENV_VAR = "SHELLFOLDER";
        const RESUME_FILE_NAME = "AUTORESUME.cmd";

        let input = "";
        process.stdin.on("data", chunk => input += chunk);
        process.stdin.on("end", () => {
            let dir = process.env[RESUME_DIR_ENV_VAR];
            if (!dir) return;
            let sessionId = JSON.parse(input).session_id;
            if (!sessionId) return;
            fs.writeFileSync(path.join(dir, RESUME_FILE_NAME), `claude -r ${sessionId}\n`);
        });
        """;

    public static bool IsInstalled
    {
        get
        {
            try
            {
                return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(HookFileName);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool ClaudeInstalled => Directory.Exists(ClaudeDir);

    public static void Install()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HookPath)!);
        File.WriteAllText(HookPath, HookScript);
        var root = (File.Exists(SettingsPath) ? JsonNode.Parse(File.ReadAllText(SettingsPath)) : null) as JsonObject ?? new JsonObject();
        if (root.ToJsonString().Contains(HookFileName))
        {
            return;
        }
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }
        if (hooks["SessionStart"] is not JsonArray sessionStart)
        {
            sessionStart = new JsonArray();
            hooks["SessionStart"] = sessionStart;
        }
        sessionStart.Add(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = "node " + HookPath.Replace('\\', '/'),
            }),
        });
        File.WriteAllText(SettingsPath, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        AppLog.Write("claude", "auto-resume hook installed at " + HookPath);
    }
}
