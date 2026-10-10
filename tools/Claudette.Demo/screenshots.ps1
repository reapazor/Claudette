<#
.SYNOPSIS
Takes the README's screenshots (Windows): the demo from claudette-demo in each style and theme, with the main window,
the diff view, the thread's Tasks page and agent map, the detailed usage header and History of each.

.DESCRIPTION
For Standard and Claude, dark and light: makes the demo content afresh, starts the Debug build of Claudette on it
(fake-claude as Claude Code, so no account and no tokens), visits every tab so each shows its model and any reviewed
icon, opens the Grappling hook tab's Changed files, and captures the window as <style>-<theme>.png, and Claudette standing on
the composer, close up, as claudette-<style>-<theme>.png (the demo runs with motion reduced, so she stands still at the
composer's right rather than being caught part-way through falling off it). Then it:
- opens the diff of StarfallCharacter.cpp from an Edit card and captures that window as diff-<style>-<theme>.png;
- selects the Move to the Input System thread, opens its side panel on Tasks and captures the window as
  tasks-<style>-<theme>.png, then on Agents, with the first subagent selected, as agents-<style>-<theme>.png;
- expands the usage header and captures the top of the window as header-<style>-<theme>.png;
- collapses it again, selects Grappling hook, opens History, picks starfall's chip and captures the window as
  history-<style>-<theme>.png;
and closes Claudette by its window, as a user would.

Captures go through PrintWindow, so other windows over Claudette's don't matter, and clicks through UI Automation.

.PARAMETER Projects
Where the demo's git repositories go. The conversation shows their paths, so choose a short one that says nothing
about you, such as C:\Demo. It must be empty, or one claudette-demo made.

.EXAMPLE
tools\Claudette.Demo\screenshots.ps1 -Projects C:\Demo
#>
param(
    [Parameter(Mandatory)] [string] $Projects,
    [string] $Out = (Join-Path $PSScriptRoot '..\..\docs\screenshots'),
    [string] $Work = (Join-Path ([IO.Path]::GetTempPath()) 'claudette-demo'),
    # How long Claudette gets to start and restore its tabs before the first click.
    [int] $SettleSeconds = 10
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WindowShot
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    /// <summary>
    /// The window as it draws itself, without the resize borders and shadow around its frame; down to
    /// <paramref name="bottom"/> on the screen when it isn't zero.
    /// </summary>
    public static void Save(IntPtr hwnd, string path, int bottom)
    {
        RECT outer, frame;
        GetWindowRect(hwnd, out outer);
        DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out frame, Marshal.SizeOf(typeof(RECT)));
        using (var full = new Bitmap(outer.Right - outer.Left, outer.Bottom - outer.Top, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
                g.ReleaseHdc(hdc);
            }
            var to = bottom == 0 ? frame.Bottom : Math.Min(bottom, frame.Bottom);
            var crop = new Rectangle(frame.Left - outer.Left, frame.Top - outer.Top, frame.Right - frame.Left, to - frame.Top);
            using (var shot = full.Clone(crop, PixelFormat.Format32bppArgb))
            {
                shot.Save(path, ImageFormat.Png);
            }
        }
    }

    /// <summary>The part of the window from (<paramref name="left"/>, <paramref name="top"/>) to (<paramref name="right"/>, <paramref name="bottom"/>) on the screen.</summary>
    public static void SaveRegion(IntPtr hwnd, string path, int left, int top, int right, int bottom)
    {
        RECT outer;
        GetWindowRect(hwnd, out outer);
        using (var full = new Bitmap(outer.Right - outer.Left, outer.Bottom - outer.Top, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
                g.ReleaseHdc(hdc);
            }
            var crop = Rectangle.Intersect(new Rectangle(left - outer.Left, top - outer.Top, right - left, bottom - top), new Rectangle(0, 0, full.Width, full.Height));
            using (var shot = full.Clone(crop, PixelFormat.Format32bppArgb))
            {
                shot.Save(path, ImageFormat.Png);
            }
        }
    }

    /// <summary>Screen pixels per device-independent pixel in the window.</summary>
    public static double Scale(IntPtr hwnd)
    {
        return GetDpiForWindow(hwnd) / 96.0;
    }

    /// <summary>Sets a window's size in device-independent pixels, keeping its place.</summary>
    public static void Resize(IntPtr hwnd, int width, int height)
    {
        var scale = Scale(hwnd);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, (int)(width * scale), (int)(height * scale), 0x0002 /* NOMOVE */ | 0x0004 /* NOZORDER */);
    }

    /// <summary>A visible top-level window of the process other than <paramref name="main"/>, or zero.</summary>
    public static IntPtr OtherWindow(int processId, IntPtr main)
    {
        var found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            RECT rect;
            GetWindowRect(hwnd, out rect);
            if (pid == processId && hwnd != main && IsWindowVisible(hwnd) && rect.Right - rect.Left > 400)
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
[WindowShot]::SetProcessDPIAware() | Out-Null

# The control named $text. "Open diff#2" is the second one.
function Find-ByText([IntPtr] $hwnd, [string] $text) {
    $index = 0
    if ($text -match '^(.*)#(\d+)$') { $text = $Matches[1]; $index = [int] $Matches[2] - 1 }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $named = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $text)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $named)
    if ($all.Count -le $index) { throw "Found $($all.Count) controls named '$text'." }
    $all[$index]
}

# The first control whose name starts with $prefix, such as History's chip for a project, "starfall, 5 sessions"; or null.
function Find-ByPrefix([IntPtr] $hwnd, [string] $prefix) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $all) {
        if ($element.Current.Name.StartsWith($prefix, [StringComparison]::Ordinal)) { return $element }
    }
    $null
}

# Clicks the control named $text, or the nearest one around it that can be clicked.
function Invoke-ByText([IntPtr] $hwnd, [string] $text) {
    Invoke-Element (Find-ByText $hwnd $text) $text
}

function Invoke-Element($element, [string] $what) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    while ($element) {
        $pattern = $null
        if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref] $pattern)) {
            $pattern.Invoke()
            return
        }
        $element = $walker.GetParent($element)
    }
    throw "Nothing to click around '$what'."
}

# Clicks the control named $button on the same row as the first text named $row that has one, such as an Edit card's
# Open diff: which of those buttons is which depends on what the conversation has scrolled into view.
function Invoke-OnRow([IntPtr] $hwnd, [string] $row, [string] $button) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    $named = { param($name) $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))) }
    $buttons = & $named $button
    foreach ($text in & $named $row) {
        $line = $text.Current.BoundingRectangle
        foreach ($candidate in $buttons) {
            $box = $candidate.Current.BoundingRectangle
            if ([Math]::Abs(($box.Top + $box.Bottom) / 2 - ($line.Top + $line.Bottom) / 2) -lt $line.Height) {
                Invoke-Element $candidate $button
                return
            }
        }
    }
    throw "No '$button' on a row with '$row'."
}

# Selects the item at $index (from 0) of the tree named $tree, such as a node of the agent map.
function Select-TreeItem([IntPtr] $hwnd, [string] $tree, [int] $index) {
    $type = { param($t) New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $t) }
    $named = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $tree)
    $found = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd).FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition($named, (& $type ([System.Windows.Automation.ControlType]::Tree)))))
    if (-not $found) { throw "Found no tree named '$tree'." }
    $items = $found.FindAll([System.Windows.Automation.TreeScope]::Descendants, (& $type ([System.Windows.Automation.ControlType]::TreeItem)))
    if ($items.Count -le $index) { throw "The tree '$tree' has $($items.Count) items." }
    $items[$index].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}

function Wait-Until([scriptblock] $condition, [string] $what, [int] $seconds = 60) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while (-not (& $condition)) {
        if ((Get-Date) -gt $deadline) { throw "Timed out waiting for $what." }
        Start-Sleep -Milliseconds 250
    }
}

Write-Host 'Building Claudette and the demo...'
& dotnet build (Join-Path $repo 'src\Claudette.App\Claudette.App.csproj') -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
& dotnet build (Join-Path $repo 'tools\Claudette.Demo\Claudette.Demo.csproj') -v q -nologo
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
$app = Join-Path $repo 'src\Claudette.App\bin\Debug\net10.0\Claudette.exe'
$demo = Join-Path $repo 'tools\Claudette.Demo\bin\Debug\net10.0\claudette-demo.exe'

foreach ($style in 'standard', 'claude') {
    foreach ($theme in 'dark', 'light') {
        Write-Host "$style $theme"
        & $demo $Work --theme $theme --style $style --projects $Projects --still | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'claudette-demo failed.' }

        $start = New-Object System.Diagnostics.ProcessStartInfo $app
        $start.UseShellExecute = $false
        $start.EnvironmentVariables['CLAUDETTE_HOME'] = Join-Path $Work 'home'
        $start.EnvironmentVariables['CLAUDE_CONFIG_DIR'] = Join-Path $Work 'claude-config'
        $start.EnvironmentVariables['FAKE_CLAUDE_USAGE'] = Join-Path $Work 'usage.json'
        $start.EnvironmentVariables['CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC'] = '1'
        # A source build otherwise copies itself to its data folder and runs from there.
        $start.EnvironmentVariables['CLAUDETTE_RUN_IN_PLACE'] = '1'
        $claudette = [System.Diagnostics.Process]::Start($start)
        try {
            Wait-Until { $claudette.Refresh(); $claudette.MainWindowHandle -ne [IntPtr]::Zero } 'the window'
            $window = $claudette.MainWindowHandle
            Start-Sleep -Seconds $SettleSeconds

            # A tab loads its session when it's first shown: visit each, then come back to the first.
            $tabs = 'Stamina drains twice on clients', 'Inventory loses items on save', 'Move to the Input System', 'UI on the Input System',
                'Gamepad rumble', 'Pool particle emitters', 'Grappling hook', 'Files (5)'
            foreach ($text in $tabs) {
                Invoke-ByText $window $text
                Start-Sleep -Seconds 2
            }
            [WindowShot]::Save($window, (Join-Path $Out "$style-$theme.png"), 0)

            # Claudette on the composer, close up: with motion reduced she stands still at its right, over Send.
            $send = (Find-ByText $window 'Send').Current.BoundingRectangle
            $scale = [WindowShot]::Scale($window)
            [WindowShot]::SaveRegion($window, (Join-Path $Out "claudette-$style-$theme.png"), [int] ($send.Right - 616 * $scale),
                [int] ($send.Top - 150 * $scale), [int] ($send.Right + 24 * $scale), [int] ($send.Bottom + 34 * $scale))

            Invoke-OnRow $window (Join-Path $Projects 'starfall\Source\Starfall\Player\StarfallCharacter.cpp') 'Open diff'
            Wait-Until { [WindowShot]::OtherWindow($claudette.Id, $window) -ne [IntPtr]::Zero } 'the diff view' 20
            $diff = [WindowShot]::OtherWindow($claudette.Id, $window)
            [WindowShot]::Resize($diff, 1000, 470)
            Start-Sleep -Seconds 2
            [WindowShot]::Save($diff, (Join-Path $Out "diff-$style-$theme.png"), 0)

            # The thread: its plan and tasks, then its subagents with the first one's details.
            Invoke-ByText $window 'Move to the Input System'
            Start-Sleep -Seconds 2
            Invoke-ByText $window 'Show the side panel'
            Start-Sleep -Seconds 1
            Invoke-ByText $window 'Tasks'
            Start-Sleep -Seconds 2
            [WindowShot]::Save($window, (Join-Path $Out "tasks-$style-$theme.png"), 0)
            Invoke-ByText $window 'Agents'
            Start-Sleep -Seconds 1
            Select-TreeItem $window 'Agents' 1
            Start-Sleep -Seconds 2
            [WindowShot]::Save($window, (Join-Path $Out "agents-$style-$theme.png"), 0)

            # The detailed usage header, down to its edge: just above the sidebar's first button.
            Invoke-ByText $window 'Expand the usage header'
            Start-Sleep -Seconds 3
            $sidebarTop = (Find-ByText $window 'New tab').Current.BoundingRectangle.Top
            [WindowShot]::Save($window, (Join-Path $Out "header-$style-$theme.png"), [int] ($sidebarTop - 10 * [WindowShot]::Scale($window)))

            # History from a starfall tab, narrowed to starfall by its chip: its tabs' sessions and older ones, one in a worktree.
            Invoke-ByText $window 'Collapse the usage header'
            Start-Sleep -Seconds 1
            Invoke-ByText $window 'Grappling hook'
            Start-Sleep -Seconds 2
            Invoke-ByText $window 'History'
            Wait-Until { Find-ByPrefix $window 'starfall, ' } 'History to list starfall' 20
            Start-Sleep -Seconds 2
            Invoke-Element (Find-ByPrefix $window 'starfall, ') 'starfall'
            Start-Sleep -Seconds 2
            [WindowShot]::Save($window, (Join-Path $Out "history-$style-$theme.png"), 0)
        }
        finally {
            # Closed by its window, so it stops each tab's Claude Code as it would for a user.
            $claudette.CloseMainWindow() | Out-Null
            if (-not $claudette.WaitForExit(30000)) { Write-Warning "Claudette (pid $($claudette.Id)) didn't close." }
        }
    }
}
Write-Host "Saved to $Out"
