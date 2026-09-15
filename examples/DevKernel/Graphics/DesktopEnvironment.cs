using System;
using System.Collections.Generic;
using System.Drawing;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System;
using Cosmos.Kernel.System.Graphics;
using Cosmos.Kernel.System.Graphics.Fonts;
using Cosmos.Kernel.System.Mouse;
using Cosmos.Kernel.System.Vfs;
using DevKernel.Shell;
using SysThread = System.Threading.Thread;

namespace DevKernel.Graphics;

/// <summary>
/// A small native desktop shell. Apps are registered as native executable
/// entries here, rather than as separate processes; the launcher and window
/// manager are deliberately simple enough to run directly on the kernel.
/// </summary>
internal static class DesktopEnvironment
{
    private const int TaskbarHeight = 42;
    private const int IconWidth = 150;
    private const int IconHeight = 86;
    private const int IconGap = 18;
    private const int WindowX = 210;
    private const int WindowY = 92;
    private const int WindowWidth = 610;
    private const int WindowHeight = 410;

    private enum AppId
    {
        Files,
        Terminal,
        Monitor,
        About,
        Disk,
    }

    private readonly struct AppEntry
    {
        public readonly AppId Id;
        public readonly string Name;
        public readonly string Executable;
        public readonly Color Accent;

        public AppEntry(AppId id, string name, string executable, Color accent)
        {
            Id = id;
            Name = name;
            Executable = executable;
            Accent = accent;
        }
    }

    private static readonly AppEntry[] s_apps =
    [
        new(AppId.Files, "Files", "files.app", Color.DeepSkyBlue),
        new(AppId.Terminal, "Terminal", "terminal.app", Color.MediumPurple),
        new(AppId.Monitor, "Monitor", "monitor.app", Color.LimeGreen),
        new(AppId.About, "About", "about.app", Color.Gold),
        new(AppId.Disk, "Disk Utility", "disk.app", Color.Orange),
    ];

    /// <summary>Runs the desktop until Escape is pressed.</summary>
    public static void Run()
    {
        if (!KernelFeatures.Graphics)
        {
            Terminal.Error("GUI unavailable: graphics support is disabled.");
            return;
        }

        Canvas canvas = Canvas.GetFullScreen();
        PCScreenFont font = PCScreenFont.DefaultFont;
        if (KernelFeatures.Mouse)
        {
            MouseManager.SetScreenSize(canvas.Width, canvas.Height);
            MouseManager.SetPosition(canvas.Width / 2, canvas.Height / 2);
        }

        AppId? openApp = null;
        bool previousLeft = false;
        int windowX = -1;
        int windowY = -1;
        bool draggingWindow = false;
        int dragOffsetX = 0;
        int dragOffsetY = 0;
        string terminalInput = string.Empty;
        var terminalLines = new List<string>
        {
            "CosmosOS terminal executable",
            "Type help, ls, pwd, info, echo <text>, or clear.",
        };

        while (true)
        {
            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                ConsoleKey key = keyInfo.Key;
                if (key == ConsoleKey.Escape)
                {
                    break;
                }

                if (key >= ConsoleKey.F1 && key <= ConsoleKey.F5)
                {
                    openApp = (AppId)(key - ConsoleKey.F1);
                    windowX = -1;
                    windowY = -1;
                }
                else if (openApp == AppId.Terminal)
                {
                    HandleTerminalKey(keyInfo, terminalLines, ref terminalInput);
                }
            }

            int mouseX = KernelFeatures.Mouse ? MouseManager.X : canvas.Width / 2;
            int mouseY = KernelFeatures.Mouse ? MouseManager.Y : canvas.Height / 2;
            bool left = KernelFeatures.Mouse && MouseManager.LeftButton;
            bool clicked = left && !previousLeft;
            previousLeft = left;

            GetWindowBounds(canvas, windowX, windowY, out int currentWindowX, out int currentWindowY, out int currentWindowWidth, out _);
            if (openApp is not null && clicked && mouseY >= currentWindowY && mouseY < currentWindowY + 36 &&
                mouseX >= currentWindowX && mouseX < currentWindowX + currentWindowWidth - 42)
            {
                draggingWindow = true;
                dragOffsetX = mouseX - currentWindowX;
                dragOffsetY = mouseY - currentWindowY;
            }

            if (!left)
            {
                draggingWindow = false;
            }

            if (draggingWindow)
            {
                windowX = mouseX - dragOffsetX;
                windowY = mouseY - dragOffsetY;
            }

            if (clicked)
            {
                if (openApp is AppId current &&
                    mouseX >= currentWindowX + currentWindowWidth - 42 &&
                    mouseX < currentWindowX + currentWindowWidth &&
                    mouseY >= currentWindowY &&
                    mouseY < currentWindowY + 36)
                {
                    openApp = null;
                    windowX = -1;
                    windowY = -1;
                }
                else
                {
                    openApp = HitTestApp(mouseX, mouseY);
                    windowX = -1;
                    windowY = -1;
                    if (openApp is null && mouseY >= canvas.Height - TaskbarHeight)
                    {
                        openApp = AppId.Terminal;
                    }
                }
            }

            canvas.Clear(Color.FromArgb(0x12, 0x18, 0x2A));
            DrawDesktop(canvas, font, openApp, mouseX, mouseY);
            if (openApp is AppId selected)
            {
                DrawWindow(canvas, font, selected, terminalLines, terminalInput, windowX, windowY);
            }

            if (KernelFeatures.Mouse)
            {
                MouseCursor.Draw(canvas, mouseX, mouseY);
            }

            canvas.Display();
            SysThread.Sleep(16);
        }

        Console.Clear();
    }

    private static AppId? HitTestApp(int x, int y)
    {
        GetGridMetrics(out int columns, out int iconWidth, out int iconHeight, out int gap);
        for (int i = 0; i < s_apps.Length; i++)
        {
            int iconX = 28 + (i % columns) * (iconWidth + gap);
            int iconY = 80 + (i / columns) * (iconHeight + gap);
            if (x >= iconX && x < iconX + iconWidth && y >= iconY && y < iconY + iconHeight)
            {
                return s_apps[i].Id;
            }
        }

        return null;
    }

    private static void DrawDesktop(Canvas canvas, PCScreenFont font, AppId? openApp, int mouseX, int mouseY)
    {
        canvas.DrawFilledRectangle(Color.FromArgb(0x1B, 0x2B, 0x45), 0, 0, canvas.Width, 58);
        canvas.DrawString("CosmosOS", font, Color.White, 26, 20);
        canvas.DrawString("Gen 3 Desktop", font, Color.FromArgb(0xA8, 0xC7, 0xE8), canvas.Width - 170, 20);

        canvas.DrawString("Native apps", font, Color.FromArgb(0xA8, 0xC7, 0xE8), 28, 62);
        GetGridMetrics(out int columns, out int iconWidth, out int iconHeight, out int gap);
        for (int i = 0; i < s_apps.Length; i++)
        {
            AppEntry app = s_apps[i];
            int x = 28 + (i % columns) * (iconWidth + gap);
            int y = 80 + (i / columns) * (iconHeight + gap);
            bool hovered = mouseX >= x && mouseX < x + iconWidth && mouseY >= y && mouseY < y + iconHeight;
            canvas.DrawFilledRectangle(hovered ? Color.FromArgb(0x2D, 0x4D, 0x70) : Color.FromArgb(0x20, 0x31, 0x4D), x, y, iconWidth, iconHeight);
            canvas.DrawRectangle(app.Accent, x, y, iconWidth, iconHeight);
            canvas.DrawFilledRectangle(app.Accent, x + 12, y + 12, 28, 28);
            canvas.DrawString(app.Name, font, Color.White, x + 60, y + 20);
            canvas.DrawString(app.Executable, font, Color.FromArgb(0xA8, 0xC7, 0xE8), x + 12, y + iconHeight - 20);
        }

        int taskbarY = canvas.Height - TaskbarHeight;
        canvas.DrawFilledRectangle(Color.FromArgb(0x20, 0x31, 0x4D), 0, taskbarY, canvas.Width, TaskbarHeight);
        canvas.DrawString("[F1-F5] launch apps", font, Color.FromArgb(0xA8, 0xC7, 0xE8), 18, taskbarY + 14);
        canvas.DrawString("[Esc] return to shell", font, Color.FromArgb(0xA8, 0xC7, 0xE8), Math.Max(18, canvas.Width - 190), taskbarY + 14);
    }

    private static void DrawWindow(Canvas canvas, PCScreenFont font, AppId appId, List<string> terminalLines, string terminalInput, int requestedX, int requestedY)
    {
        AppEntry app = s_apps[(int)appId];
        GetWindowBounds(canvas, requestedX, requestedY, out int windowX, out int windowY, out int windowWidth, out int windowHeight);
        canvas.DrawFilledRectangle(Color.FromArgb(0x08, 0x0D, 0x17), windowX + 6, windowY + 8, windowWidth, windowHeight);
        canvas.DrawFilledRectangle(Color.FromArgb(0x26, 0x3D, 0x5C), windowX, windowY, windowWidth, 36);
        canvas.DrawRectangle(app.Accent, windowX, windowY, windowWidth, windowHeight);
        canvas.DrawString(app.Name + "  -  " + app.Executable, font, Color.White, windowX + 16, windowY + 12);
        canvas.DrawString("-", font, Color.White, windowX + windowWidth - 48, windowY + 12);
        canvas.DrawString("X", font, Color.FromArgb(0xFF, 0xA0, 0xA0), windowX + windowWidth - 24, windowY + 12);

        int x = windowX + 22;
        int y = windowY + 62;
        switch (appId)
        {
            case AppId.Files:
                canvas.DrawString("File system", font, Color.Cyan, x, y);
                List<string> fileLines = ReadFileSystemLines();
                for (int i = 0; i < fileLines.Count && i < 12; i++)
                {
                    canvas.DrawString(fileLines[i], font, Color.White, x, y + 30 + i * 24);
                }
                break;
            case AppId.Terminal:
                int first = terminalLines.Count > 10 ? terminalLines.Count - 10 : 0;
                int row = 0;
                for (int i = first; i < terminalLines.Count; i++)
                {
                    canvas.DrawString(terminalLines[i], font, Color.White, x, y + row * 24);
                    row++;
                }

                canvas.DrawString("> " + terminalInput + "_", font, Color.LimeGreen, x, windowY + windowHeight - 42);
                break;
            case AppId.Monitor:
                canvas.DrawString("System monitor executable", font, Color.Cyan, x, y);
                canvas.DrawString("CPU scheduler: active", font, Color.White, x, y + 34);
                canvas.DrawString("Framebuffer: " + canvas.Width + "x" + canvas.Height, font, Color.White, x, y + 58);
                canvas.DrawString("Graphics: native canvas", font, Color.White, x, y + 82);
                canvas.DrawString("Mouse: " + (KernelFeatures.Mouse ? "connected" : "unavailable"), font, Color.White, x, y + 106);
                break;
            case AppId.About:
                canvas.DrawString("CosmosOS Gen 3", font, Color.Gold, x, y);
                canvas.DrawString("A native AOT graphical operating system demo.", font, Color.White, x, y + 34);
                canvas.DrawString("Window manager + launcher + native app registry", font, Color.White, x, y + 58);
                canvas.DrawString("Build: DevKernel", font, Color.FromArgb(0xA8, 0xC7, 0xE8), x, y + 100);
                break;
            case AppId.Disk:
                canvas.DrawString("Disk Utility", font, Color.Orange, x, y);
                canvas.DrawString("Mounted volumes", font, Color.Cyan, x, y + 34);
                IReadOnlyList<VfsManager.VfsMount> mounts = VfsManager.Mounts;
                if (mounts.Count == 0)
                {
                    canvas.DrawString("No disks mounted", font, Color.White, x, y + 68);
                }
                else
                {
                    for (int i = 0; i < mounts.Count && i < 8; i++)
                    {
                        VfsManager.VfsMount mount = mounts[i];
                        canvas.DrawString(mount.MountPoint + "  " + mount.Name, font, Color.White, x, y + 68 + i * 24);
                    }
                }
                break;
        }
    }

    private static void GetGridMetrics(out int columns, out int iconWidth, out int iconHeight, out int gap)
    {
        columns = 3;
        gap = 12;
        iconWidth = 150;
        iconHeight = 72;
    }

    private static void GetWindowBounds(Canvas canvas, int requestedX, int requestedY, out int x, out int y, out int width, out int height)
    {
        width = Math.Min(WindowWidth, Math.Max(260, canvas.Width - 24));
        height = Math.Min(WindowHeight, Math.Max(220, canvas.Height - TaskbarHeight - 80));
        x = requestedX < 0 ? (canvas.Width - width) / 2 : Math.Max(8, Math.Min(requestedX, canvas.Width - width - 8));
        y = requestedY < 0 ? 70 : Math.Max(62, Math.Min(requestedY, canvas.Height - TaskbarHeight - height - 8));
    }

    private static void HandleTerminalKey(ConsoleKeyInfo key, List<string> output, ref string input)
    {
        if (key.Key == ConsoleKey.Backspace)
        {
            if (input.Length > 0)
            {
                input = input.Substring(0, input.Length - 1);
            }

            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            output.Add("> " + input);
            ExecuteTerminalCommand(input, output);
            input = string.Empty;
            return;
        }

        if (!char.IsControl(key.KeyChar) && input.Length < 54)
        {
            input += key.KeyChar;
        }
    }

    private static void ExecuteTerminalCommand(string input, List<string> output)
    {
        string command = input.Trim();
        if (command.Length == 0)
        {
            return;
        }

        if (command == "clear")
        {
            output.Clear();
            return;
        }

        if (command == "help")
        {
            output.Add("help  ls  pwd  info  echo <text>  clear");
            return;
        }

        if (command == "pwd")
        {
            output.Add("/");
            return;
        }

        if (command == "ls")
        {
            output.AddRange(ReadFileSystemLines());
            return;
        }

        if (command == "info")
        {
            output.Add("CosmosOS Gen 3 / DevKernel");
            output.Add("Native AOT kernel + graphical desktop");
            return;
        }

        if (command.StartsWith("echo ", StringComparison.Ordinal))
        {
            output.Add(command.Substring(5));
            return;
        }

        output.Add("command not found: " + command);
    }

    private static List<string> ReadFileSystemLines()
    {
        var lines = new List<string> { "Mounts:" };
        IReadOnlyList<VfsManager.VfsMount> mounts = VfsManager.Mounts;
        if (mounts.Count == 0)
        {
            lines.Add("  (no mounted filesystems)");
            return lines;
        }

        for (int i = 0; i < mounts.Count; i++)
        {
            VfsManager.VfsMount mount = mounts[i];
            lines.Add("  [MOUNT] " + mount.MountPoint + " (" + mount.Name + ")");
            if (!VfsManager.TryOpenDirectory(mount.MountPoint, out IVfsDirectoryHandle? directory) ||
                !directory.TryReadDir(out IReadOnlyList<IVfsInode> entries))
            {
                continue;
            }

            for (int entry = 0; entry < entries.Count && entry < 8; entry++)
            {
                lines.Add("    " + entries[entry].Name);
            }
        }

        return lines;
    }
}
