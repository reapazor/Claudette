using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Claudette.Core.ProjectTools;
using Claudette.Platform.Notifications.Windows;
using Microsoft.Extensions.Logging;

namespace Claudette.Platform.Shell.Windows;

/// <summary>
/// The taskbar jump list (DESIGN.md §4): a "Recent folders" category whose items launch Claudette with
/// <c>--folder &lt;path&gt;</c>, which a running Claudette picks up (<see cref="SingleInstance"/>). Built with
/// <c>ICustomDestinationList</c> and shell links.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsJumpList(string executablePath, ILogger logger) : IJumpList
{
    public const string Category = "Recent folders";

    private static readonly Guid DestinationListClsid = new("77F10CF0-3DB5-4966-B520-B7C54FD35ED6");
    private static readonly Guid ObjectCollectionClsid = new("2D3468C1-36A7-43B6-AC24-D3F02FD9607A");
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    /// <summary>PKEY_Title: the text a jump list item shows.</summary>
    private static readonly PropertyKey TitleKey = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 2);

    public void Update(IReadOnlyList<RecentFolderEntry> folders)
    {
        try
        {
            var list = Build(folders, out _);
            list.CommitList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't update the jump list.");
        }
    }

    /// <summary>Builds the list up to the point of committing it, so a test can check it without changing the taskbar.</summary>
    internal ICustomDestinationList Build(IReadOnlyList<RecentFolderEntry> folders, out int added)
    {
        var list = WinRt.CreateComInstance<ICustomDestinationList>(DestinationListClsid);
        if (!WindowsAppIdentity.IsPackaged)
        {
            list.SetAppID(WindowsAppIdentity.AppUserModelId);
        }
        list.BeginList(out var slots, typeof(IObjectArray).GUID, out var removed);
        if (removed != 0)
        {
            Marshal.Release(removed);
        }
        var collection = WinRt.CreateComInstance<IObjectCollection>(ObjectCollectionClsid);
        added = 0;
        foreach (var folder in folders.Take((int)Math.Max(slots, 1)))
        {
            collection.AddObject(CreateLink(folder));
            added++;
        }
        if (added > 0)
        {
            list.AppendCategory(Category, (IObjectArray)collection);
        }
        return list;
    }

    private IShellLinkW CreateLink(RecentFolderEntry folder)
    {
        var link = WinRt.CreateComInstance<IShellLinkW>(ShellLinkClsid);
        link.SetPath(executablePath);
        link.SetArguments($"--folder {CommandLines.QuoteForWindows(folder.Path)}");
        link.SetDescription(folder.Path);
        link.SetIconLocation(Path.Combine(Environment.SystemDirectory, "shell32.dll"), 3);
        var properties = (IPropertyStore)link;
        var title = PropVariant.FromString(folder.Label);
        try
        {
            properties.SetValue(TitleKey, title);
            properties.Commit();
        }
        finally
        {
            title.Clear();
        }
        return link;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

/// <summary>A PROPVARIANT holding a string (VT_LPWSTR), the only kind the jump list needs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    private const ushort VtLpwstr = 31;

    private ushort _type;
    private ushort _reserved1;
    private ushort _reserved2;
    private ushort _reserved3;
    private nint _value;
    private nint _value2;

    public static PropVariant FromString(string value) => new() { _type = VtLpwstr, _value = Marshal.StringToCoTaskMemUni(value) };

    public void Clear()
    {
        if (_type == VtLpwstr && _value != 0)
        {
            Marshal.FreeCoTaskMem(_value);
        }
        this = default;
    }
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("6332DEBF-87B5-4670-90C0-5E57B408A49E")]
internal partial interface ICustomDestinationList
{
    void SetAppID(string appId);

    void BeginList(out uint minSlots, in Guid riid, out nint removedDestinations);

    void AppendCategory(string category, IObjectArray items);

    void AppendKnownCategory(int category);

    void AddUserTasks(IObjectArray tasks);

    void CommitList();

    void GetRemovedDestinations(in Guid riid, out nint removed);

    void DeleteList(string? appId);

    void AbortList();
}

[GeneratedComInterface]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal partial interface IObjectArray
{
    void GetCount(out uint count);

    void GetAt(uint index, in Guid riid, out nint item);
}

[GeneratedComInterface]
[Guid("5632B1A4-E38A-400A-928A-D4CD63230295")]
internal partial interface IObjectCollection : IObjectArray
{
    void AddObject(IShellLinkW item);

    void AddFromArray(IObjectArray source);

    void RemoveObjectAt(uint index);

    void Clear();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal partial interface IShellLinkW
{
    void GetPath(nint file, int maxPath, nint findData, uint flags);

    void GetIDList(out nint idList);

    void SetIDList(nint idList);

    void GetDescription(nint name, int maxName);

    void SetDescription(string name);

    void GetWorkingDirectory(nint directory, int maxPath);

    void SetWorkingDirectory(string directory);

    void GetArguments(nint arguments, int maxPath);

    void SetArguments(string arguments);

    void GetHotkey(out ushort hotkey);

    void SetHotkey(ushort hotkey);

    void GetShowCmd(out int showCmd);

    void SetShowCmd(int showCmd);

    void GetIconLocation(nint iconPath, int maxPath, out int iconIndex);

    void SetIconLocation(string iconPath, int iconIndex);

    void SetRelativePath(string relativePath, uint reserved);

    void Resolve(nint hwnd, uint flags);

    void SetPath(string file);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    void GetCount(out uint count);

    void GetAt(uint index, out PropertyKey key);

    void GetValue(in PropertyKey key, out PropVariant value);

    void SetValue(in PropertyKey key, in PropVariant value);

    void Commit();
}
