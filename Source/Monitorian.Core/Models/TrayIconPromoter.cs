using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Monitorian.Core.Models;

/// <summary>
/// Keeps this app's notification area (tray) icon in the visible part of the taskbar
/// instead of the "Show hidden icons" overflow.
/// </summary>
/// <remarks>
/// Windows has no public API for an app to promote its own tray icon. On Windows 11, the
/// per-icon visibility the user sets by dragging an icon (or in Settings > Personalization >
/// Taskbar > Other system tray icons) is stored under HKCU\Control Panel\NotifyIconSettings,
/// one subkey per icon, keyed by the executable path. This sets IsPromoted = 1 on the entries
/// for this executable. It is undocumented and simply does nothing where the key does not exist
/// (e.g. Windows 10). Remove the call in AppController if you prefer to manage it manually.
/// </remarks>
public static class TrayIconPromoter
{
	private const string SettingsKey = @"Control Panel\NotifyIconSettings";
	private const string ExecutablePathName = "ExecutablePath";
	private const string IsPromotedName = "IsPromoted";

	/// <summary>
	/// Waits so that Windows has created the entry for the newly added icon, then promotes it.
	/// </summary>
	/// <param name="delay">Delay before promoting</param>
	/// <remarks>Never throws.</remarks>
	public static async Task PromoteAsync(TimeSpan delay)
	{
		try
		{
			await Task.Delay(delay).ConfigureAwait(false);

			var fileName = Path.GetFileName(Assembly.GetEntryAssembly().Location);
			if (string.IsNullOrEmpty(fileName))
				return;

			using var root = Registry.CurrentUser.OpenSubKey(SettingsKey, false);
			if (root is null)
				return;

			foreach (var subKeyName in root.GetSubKeyNames())
			{
				using var subKey = root.OpenSubKey(subKeyName, true);

				// The stored path may begin with a known folder GUID instead of a drive letter
				// (e.g. "{7C5A40EF-...}\Monitorian\Monitorian.exe" for Program Files (x86)),
				// so match on the file name.
				if (subKey?.GetValue(ExecutablePathName) is not string path)
					continue;
				if (!path.EndsWith(@"\" + fileName, StringComparison.OrdinalIgnoreCase))
					continue;
				if (subKey.GetValue(IsPromotedName) is int value && value == 1)
					continue;

				subKey.SetValue(IsPromotedName, 1, RegistryValueKind.DWord);
			}
		}
		catch (Exception ex)
		{
			// Never let this affect the app (unobserved task exceptions are configured to throw).
			Debug.WriteLine($"Failed to promote tray icon. {ex}");
		}
	}
}
