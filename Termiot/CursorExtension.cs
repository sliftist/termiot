using System.IO;

namespace Termiot;

// A minimal Cursor/VS Code extension dropped into the user's extensions folder: it binds Ctrl+Shift+C to an in-process HTTP request to our open-tab endpoint — no shell window, no process spawn, instant. Cursor picks the folder up on its next restart.
public static class CursorExtension
{
    private const string FolderName = "termiot.termiot-open-1.0.0";

    public static string ExtensionDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "extensions", FolderName);

    public static bool IsInstalled => File.Exists(Path.Combine(ExtensionDir, "package.json"));

    private const string PackageJson =
        """
        {
            "name": "termiot-open",
            "displayName": "Open Termiot here",
            "description": "Opens a Termiot tab in the current workspace folder via the local open-tab endpoint.",
            "version": "1.0.0",
            "publisher": "termiot",
            "engines": { "vscode": "^1.70.0" },
            "main": "./extension.js",
            "activationEvents": ["onCommand:termiot.open"],
            "contributes": {
                "commands": [{ "command": "termiot.open", "title": "Open Termiot here" }],
                "keybindings": [{ "command": "termiot.open", "key": "ctrl+shift+c" }]
            }
        }
        """;

    private static string BuildExtensionJs()
    {
        var batPath = StableLauncher.BatPath.Replace("\\", "\\\\");
        return
            $$"""
            const vscode = require('vscode');
            const http = require('http');
            const os = require('os');
            const cp = require('child_process');

            // A running Termiot takes the request over HTTP (instant, no process). Otherwise fall back to the version-stable launcher bat, which always points at the newest build.
            const LAUNCHER = '{{batPath}}';

            function activate(context) {
                context.subscriptions.push(vscode.commands.registerCommand('termiot.open', () => {
                    const editorPath = vscode.window.activeTextEditor?.document?.uri?.fsPath;
                    const folder = editorPath
                        ? vscode.workspace.getWorkspaceFolder(vscode.Uri.file(editorPath))?.uri?.fsPath
                        : undefined;
                    let dir = folder ?? vscode.workspace.workspaceFolders?.[0]?.uri?.fsPath ?? os.homedir();
                    // VS Code's Uri.fsPath lowercases the Windows drive letter; restore it so the path keeps its real casing.
                    dir = dir.replace(/^([a-z]):/, (m, d) => d.toUpperCase() + ':');
                    const req = http.get('http://127.0.0.1:{{HttpOpenHost.Port}}/open?dir=' + encodeURIComponent(dir), () => {});
                    req.on('error', () => {
                        try {
                            cp.spawn('cmd.exe', ['/c', LAUNCHER], { cwd: dir, detached: true, stdio: 'ignore', windowsHide: true }).unref();
                        } catch (err) {
                            vscode.window.showWarningMessage('Termiot launch failed: ' + err);
                        }
                    });
                }));
            }

            module.exports = { activate };
            """;
    }

    public static void Install()
    {
        StableLauncher.EnsureNewest();
        Directory.CreateDirectory(ExtensionDir);
        File.WriteAllText(Path.Combine(ExtensionDir, "package.json"), PackageJson);
        File.WriteAllText(Path.Combine(ExtensionDir, "extension.js"), BuildExtensionJs());
        AppLog.Write("cursor", "extension installed at " + ExtensionDir);
    }

    // Renderer startup calls this so an installed extension follows code changes without user action; Cursor loads the new file on its next restart.
    public static void EnsureUpToDate()
    {
        try
        {
            if (!IsInstalled)
            {
                return;
            }
            var jsPath = Path.Combine(ExtensionDir, "extension.js");
            var current = File.Exists(jsPath) ? File.ReadAllText(jsPath) : "";
            if (current != BuildExtensionJs() || File.ReadAllText(Path.Combine(ExtensionDir, "package.json")) != PackageJson)
            {
                Install();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("cursor", "extension refresh failed: " + ex.Message);
        }
    }

    public static void Uninstall()
    {
        if (Directory.Exists(ExtensionDir))
        {
            Directory.Delete(ExtensionDir, true);
            AppLog.Write("cursor", "extension uninstalled");
        }
    }
}
