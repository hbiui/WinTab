using System;
using System.Drawing;
using System.Windows.Automation;
using WinTab.Helpers;
using WinTab.WinAPI;

namespace WinTab.Hooks;

/// <summary>A file-list item captured under the cursor, valid only while its view identity holds.</summary>
internal sealed record ExplorerFileHoverItem(
    WindowIdentity Window,
    WindowIdentity Tab,
    WindowIdentity View,
    string Name,
    Point Point)
{
    internal bool IsCurrent() =>
        Window.IsCurrent && Tab.IsCurrent && View.IsCurrent &&
        WinAPI.WinApi.GetParent(Tab.Handle) == Window.Handle &&
        ExplorerNavigationAccess.ActiveTab(Window.Handle) == Tab.Handle;
}

/// <summary>
/// Resolves the hovered file-list item with UIA. Only real list items are accepted; scroll bars, headers,
/// group headers and every other control fail closed, mirroring the empty-area hit test's strictness.
/// </summary>
internal static class ExplorerFileHoverIdentity
{
    internal static bool TryCapture(Point point, out ExplorerFileHoverItem? item)
    {
        item = null;
        try
        {
            var hit = WinApi.WindowFromPoint(point);
            if (hit == 0 || !ExplorerFileListAutomation.TryFindFileView(hit, out var viewHandle, out var tabHandle))
                return false;

            var windowHandle = WinApi.GetAncestor(tabHandle, WinApi.GA_ROOT);
            if (windowHandle == 0 || WinApi.GetParent(tabHandle) != windowHandle ||
                !ExplorerWindowDiscovery.IsShownExplorerWindow(windowHandle) ||
                ExplorerNavigationAccess.ActiveTab(windowHandle) != tabHandle)
                return false;

            var name = FindItemName(point, viewHandle);
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var window = WindowIdentity.Capture(windowHandle);
            var tab = WindowIdentity.Capture(tabHandle);
            var view = WindowIdentity.Capture(viewHandle);
            if (!window.IsCurrent || !tab.IsCurrent || !view.IsCurrent)
                return false;

            item = new ExplorerFileHoverItem(window, tab, view, name, point);
            return true;
        }
        catch (Exception exception)
        {
            ExplorerDebugLog.Write($"Hover identity failed: {exception.GetType().Name}:{exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Walks from the element under the point up to the owning ListItem/DataItem, never crossing into another
    /// view than the captured one. Interactive chrome (buttons, headers, scroll bars) rejects immediately.
    /// </summary>
    private static string? FindItemName(Point point, nint viewHandle)
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        if (element == null)
            return null;

        var walker = TreeWalker.RawViewWalker;
        var current = element;
        for (var depth = 0; current != null && depth < 64; depth++)
        {
            var properties = current.Current;
            if (properties.ControlType == ControlType.Edit)
            {
                // Win11 renders every details-view cell as an Edit("UIProperty") placeholder, so a
                // hover on the name column lands here first. Only a live rename (keyboard focus
                // inside the box) is interactive chrome; otherwise keep walking up to the item.
                if (properties.HasKeyboardFocus)
                    return null;
            }
            else if (IsChrome(properties.ControlType))
            {
                return null;
            }

            switch (properties.ControlType)
            {
                case var type when type == ControlType.ListItem || type == ControlType.DataItem:
                    var name = properties.Name;
                    if (string.IsNullOrWhiteSpace(name))
                        return null;
                    return ReachableView(current, viewHandle) ? name : null;
            }

            if (properties.NativeWindowHandle == viewHandle)
                return null;

            current = walker.GetParent(current);
        }
        return null;
    }

    /// <summary>Confirms the item element ultimately belongs to the captured view's native window.</summary>
    private static bool ReachableView(AutomationElement element, nint viewHandle)
    {
        var walker = TreeWalker.RawViewWalker;
        var current = element;
        for (var depth = 0; current != null && depth < 64; depth++)
        {
            if (current.Current.NativeWindowHandle == viewHandle)
                return true;
            current = walker.GetParent(current);
        }
        return false;
    }

    private static bool IsChrome(ControlType type) =>
        type == ControlType.TreeItem || type == ControlType.Button || type == ControlType.Hyperlink ||
        type == ControlType.Edit || type == ControlType.ComboBox || type == ControlType.CheckBox ||
        type == ControlType.ScrollBar || type == ControlType.Thumb || type == ControlType.Header ||
        type == ControlType.HeaderItem || type == ControlType.Menu || type == ControlType.MenuItem;

    internal static bool IsSameItem(ExplorerFileHoverItem? first, ExplorerFileHoverItem? second)
    {
        if (first is null || second is null)
            return false;
        return first.Window.Handle == second.Window.Handle &&
               first.Tab.Handle == second.Tab.Handle &&
               first.View.Handle == second.View.Handle &&
               StringComparer.OrdinalIgnoreCase.Equals(first.Name, second.Name);
    }
}
