using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace StartupAgency.Worker;

/// <summary>
/// Registry worker
/// </summary>
internal class RegistryWorker : IStartupWorker
{
	private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run"; // HKCU
	private const string StartupApproved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; // HKCU
	private const string Option = "/startup";

	private readonly string _name;
	private readonly string _path;
	private readonly string _pathWithOption;

	public RegistryWorker(string name, string path)
	{
		if (string.IsNullOrWhiteSpace(name))
			throw new ArgumentNullException(nameof(name));
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentNullException(nameof(path));

		this._name = name;
		this._path = path;

		// Quote the path. The default install location is "C:\Program Files (x86)\Monitorian\",
		// which contains spaces and parentheses. An unquoted command line in the Run key relies on
		// Windows guessing where the executable path ends, which is fragile.
		this._pathWithOption = $"\"{path}\" {Option}";
	}

	public bool? IsStartedOnSignIn()
	{
		return Environment.GetCommandLineArgs().Skip(1).Contains(Option);
	}

	public bool CanRegister() => true;

	public bool? IsRegistered()
	{
		using var key = Registry.CurrentUser.OpenSubKey(Run, false);
		var value = key?.GetValue(_name) as string;
		if (!PointsToThisExecutable(value))
			return false;

		// The Run value alone is not enough. If the user (or another tool) turned the entry off in
		// Settings > Apps > Startup or Task Manager > Startup apps, Windows keeps the Run value but
		// marks it disabled under StartupApproved, and the app will not start on sign in.
		return !IsDisabledByStartupApproved();
	}

	public bool Register()
	{
		if (IsRegistered() is true)
			return false;

		using (var key = Registry.CurrentUser.CreateSubKey(Run, true))
		{
			key.SetValue(_name, _pathWithOption, RegistryValueKind.String);
		}

		// The user explicitly asked to start on sign in, so clear any "disabled" flag Windows holds.
		// A missing StartupApproved value is treated by Windows as enabled.
		DeleteStartupApproved();
		return true;
	}

	public void Unregister()
	{
		using (var key = Registry.CurrentUser.OpenSubKey(Run, true))
		{
			if (key?.GetValueNames().Contains(_name) is true) // The content of value doesn't matter.
				key.DeleteValue(_name, false);
		}
		DeleteStartupApproved();
	}

	/// <summary>
	/// Rewrites the Run value if it exists but no longer launches this executable correctly
	/// (written unquoted by an older build, or pointing to an executable that no longer exists).
	/// </summary>
	/// <remarks>
	/// Does nothing if the user has not registered or has disabled the entry in Windows settings.
	/// </remarks>
	public void RepairIfStale()
	{
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(Run, true);
			if (key?.GetValue(_name) is not string value)
				return;

			if (string.Equals(value, _pathWithOption, StringComparison.OrdinalIgnoreCase))
				return;

			if (IsDisabledByStartupApproved())
				return;

			// Only take over the entry if it is this executable written in the legacy unquoted form,
			// or if the executable it points to no longer exists (app was moved or reinstalled elsewhere).
			// This avoids a second copy (e.g. a debug build) hijacking a working registration.
			var isLegacyForm = string.Equals(value, $"{_path} {Option}", StringComparison.OrdinalIgnoreCase);
			var target = ExtractExecutablePath(value);
			var isBroken = string.IsNullOrEmpty(target) || !File.Exists(target);
			if (!isLegacyForm && !isBroken)
				return;

			key.SetValue(_name, _pathWithOption, RegistryValueKind.String);
		}
		catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
		{
			// Never block app start because of this.
		}
	}

	private bool PointsToThisExecutable(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return false;

		if (string.Equals(value, _pathWithOption, StringComparison.OrdinalIgnoreCase))
			return true;

		// Accept the legacy unquoted form so existing registrations are recognized
		// (RepairIfStale will upgrade them to the quoted form).
		return string.Equals(value, $"{_path} {Option}", StringComparison.OrdinalIgnoreCase);
	}

	private static string ExtractExecutablePath(string value)
	{
		value = value.Trim();
		if (value.StartsWith("\""))
		{
			var end = value.IndexOf('"', 1);
			return (end > 1) ? value.Substring(1, end - 1) : null;
		}

		var index = value.LastIndexOf(" " + Option, StringComparison.OrdinalIgnoreCase);
		return (index > 0) ? value.Substring(0, index) : value;
	}

	private bool IsDisabledByStartupApproved()
	{
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(StartupApproved, false);
			if (key?.GetValue(_name) is not byte[] { Length: > 0 } data)
				return false;

			// First byte: 0x02 (or 0x06) = enabled, 0x03 (or 0x07) = disabled.
			return (data[0] & 0x01) == 0x01;
		}
		catch
		{
			return false;
		}
	}

	private void DeleteStartupApproved()
	{
		try
		{
			using var key = Registry.CurrentUser.OpenSubKey(StartupApproved, true);
			if (key?.GetValueNames().Contains(_name) is true)
				key.DeleteValue(_name, false);
		}
		catch
		{
		}
	}
}
