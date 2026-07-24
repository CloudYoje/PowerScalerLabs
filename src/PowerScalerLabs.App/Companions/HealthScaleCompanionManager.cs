using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace PowerScalerLabs.App.Companions;

public enum HealthScaleCompanionState
{
    NotConfigured,
    InvalidGameFolder,
    PayloadUnavailable,
    NotInstalled,
    InstalledVerified,
    InstalledUnmanaged,
    Conflict,
    Error
}

public sealed record HealthScaleCompanionStatus(
    HealthScaleCompanionState State,
    string StateText,
    string Detail,
    string GameBinPath,
    string PayloadPath,
    string InstalledPath,
    string PayloadHash,
    string InstalledHash,
    bool GameRunning,
    bool ManagedInstallation,
    bool CanInstall,
    bool CanUninstall,
    bool CanVerify);

public sealed record HealthScaleUninstallResult(bool ConfigurationRemoved, bool ConfigurationPreserved);

public sealed class HealthScaleCompanionManager
{
    public const string CompanionVersion = "1.1.1";
    public const string RuntimeFileName = "xinput_other.dll";
    public const string ConfigurationFileName = "HealthScale.ini";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly string _receiptPath;
    private readonly string _payloadDirectory;
    private HealthScaleCompanionSettings _settings;

    public HealthScaleCompanionManager(string persistentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistentRoot);

        string companionStateDirectory = Path.Combine(persistentRoot, "Companion Apps", "HealthScale");
        Directory.CreateDirectory(companionStateDirectory);
        _settingsPath = Path.Combine(companionStateDirectory, "settings.json");
        _receiptPath = Path.Combine(companionStateDirectory, "install-receipt.json");
        _payloadDirectory = Path.Combine(AppContext.BaseDirectory, "Companions", "HealthScale", "Payload");
        _settings = LoadJson<HealthScaleCompanionSettings>(_settingsPath) ?? new HealthScaleCompanionSettings();
    }

    public string ConfiguredGameBinPath => _settings.GameBinPath;

    public string PayloadDirectory => _payloadDirectory;

    public string DocumentationDirectory => Path.Combine(AppContext.BaseDirectory, "Companions", "HealthScale");

    public HealthScaleCompanionStatus Refresh()
    {
        try
        {
            string payloadPath = Path.Combine(_payloadDirectory, RuntimeFileName);
            string configuredPath = _settings.GameBinPath;
            string normalizedPath = NormalizeGameBinPath(configuredPath) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                string detectedPath = TryDetectGameBinPath();
                if (!string.IsNullOrWhiteSpace(detectedPath))
                {
                    normalizedPath = detectedPath;
                    _settings.GameBinPath = detectedPath;
                    SaveJsonAtomic(_settingsPath, _settings);
                }
            }

            bool gameRunning = IsGameRunning();
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                HealthScaleCompanionState state = string.IsNullOrWhiteSpace(configuredPath)
                    ? HealthScaleCompanionState.NotConfigured
                    : HealthScaleCompanionState.InvalidGameFolder;
                string detail = state == HealthScaleCompanionState.NotConfigured
                    ? "Select the DB Xenoverse 2 folder or its bin folder. PowerScaler Labs will not install anything until a valid DBXV2.exe is found."
                    : "The saved folder no longer contains DBXV2.exe. Select the game folder again.";
                return CreateStatus(state, detail, string.Empty, payloadPath, string.Empty, string.Empty, string.Empty, gameRunning, false, false, false, false);
            }

            if (!File.Exists(payloadPath))
            {
                return CreateStatus(
                    HealthScaleCompanionState.PayloadUnavailable,
                    "The HealthScale payload was not published with this build. Run PUBLISH_WINDOWS.cmd on a Visual Studio C++ build machine.",
                    normalizedPath,
                    payloadPath,
                    Path.Combine(normalizedPath, RuntimeFileName),
                    string.Empty,
                    string.Empty,
                    gameRunning,
                    false,
                    false,
                    false,
                    false);
            }

            string payloadHash = ComputeSha256(payloadPath);
            string installedPath = Path.Combine(normalizedPath, RuntimeFileName);
            if (!File.Exists(installedPath))
            {
                return CreateStatus(
                    HealthScaleCompanionState.NotInstalled,
                    gameRunning
                        ? "HealthScale is not installed. Close DBXV2 before installing the companion."
                        : "HealthScale is ready to install as a separate health-only companion.",
                    normalizedPath,
                    payloadPath,
                    installedPath,
                    payloadHash,
                    string.Empty,
                    gameRunning,
                    false,
                    !gameRunning,
                    false,
                    false);
            }

            string installedHash = ComputeSha256(installedPath);
            HealthScaleInstallReceipt? receipt = LoadJson<HealthScaleInstallReceipt>(_receiptPath);
            bool receiptMatches = ReceiptMatches(receipt, normalizedPath, installedHash);

            if (string.Equals(installedHash, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                HealthScaleCompanionState state = receiptMatches
                    ? HealthScaleCompanionState.InstalledVerified
                    : HealthScaleCompanionState.InstalledUnmanaged;
                string detail = receiptMatches
                    ? "Installed DLL matches the bundled HealthScale 1.1.1 payload. The source and runtime remain separate from PowerScaler Labs."
                    : "The installed DLL matches the bundled payload, but no PowerScaler Labs receipt owns it. Choose Install / Adopt to create a managed receipt before uninstalling through this app.";
                return CreateStatus(
                    state,
                    detail,
                    normalizedPath,
                    payloadPath,
                    installedPath,
                    payloadHash,
                    installedHash,
                    gameRunning,
                    receiptMatches,
                    !receiptMatches && !gameRunning,
                    receiptMatches && !gameRunning,
                    true);
            }

            return CreateStatus(
                HealthScaleCompanionState.Conflict,
                "An unrecognized xinput_other.dll already exists. PowerScaler Labs will not overwrite or remove it.",
                normalizedPath,
                payloadPath,
                installedPath,
                payloadHash,
                installedHash,
                gameRunning,
                false,
                false,
                false,
                true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            return CreateStatus(
                HealthScaleCompanionState.Error,
                exception.Message,
                _settings.GameBinPath,
                Path.Combine(_payloadDirectory, RuntimeFileName),
                string.IsNullOrWhiteSpace(_settings.GameBinPath) ? string.Empty : Path.Combine(_settings.GameBinPath, RuntimeFileName),
                string.Empty,
                string.Empty,
                IsGameRunning(),
                false,
                false,
                false,
                false);
        }
    }

    public string ConfigureGameLocation(string selectedPath)
    {
        string normalizedPath = NormalizeGameBinPath(selectedPath)
            ?? throw new InvalidOperationException("Select the DB Xenoverse 2 folder or its bin folder. The selected location must contain bin\\DBXV2.exe or DBXV2.exe.");

        _settings.GameBinPath = normalizedPath;
        SaveJsonAtomic(_settingsPath, _settings);
        return normalizedPath;
    }

    public HealthScaleCompanionStatus InstallOrAdopt()
    {
        HealthScaleCompanionStatus status = Refresh();
        if (status.GameRunning)
        {
            throw new InvalidOperationException("Close DBXV2 before installing or adopting HealthScale.");
        }
        if (status.State is HealthScaleCompanionState.NotConfigured or HealthScaleCompanionState.InvalidGameFolder)
        {
            throw new InvalidOperationException("Choose a valid DBXV2 installation before installing HealthScale.");
        }
        if (status.State == HealthScaleCompanionState.PayloadUnavailable)
        {
            throw new InvalidOperationException("The HealthScale payload is unavailable. Publish the Windows package with Visual C++ first.");
        }
        string gameVersion = FileVersionInfo.GetVersionInfo(Path.Combine(status.GameBinPath, "DBXV2.exe")).FileVersion ?? string.Empty;
        if (!IsSupportedGameVersion(gameVersion))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(gameVersion)
                    ? "DBXV2.exe has no readable file version. HealthScale 1.1.1 supports DBXV2 1.25.2.0, so installation is blocked."
                    : $"DBXV2 {gameVersion} is not the supported 1.25.2.0 build. HealthScale installation is blocked.");
        }
        if (status.State == HealthScaleCompanionState.Conflict)
        {
            throw new InvalidOperationException("An unknown xinput_other.dll is already installed. It was not overwritten.");
        }
        if (status.State == HealthScaleCompanionState.Error)
        {
            throw new InvalidOperationException(status.Detail);
        }

        string payloadDll = status.PayloadPath;
        string destinationDll = status.InstalledPath;
        string payloadIni = Path.Combine(_payloadDirectory, ConfigurationFileName);
        string destinationIni = Path.Combine(status.GameBinPath, ConfigurationFileName);

        if (!File.Exists(destinationDll))
        {
            CopyFileAtomic(payloadDll, destinationDll);
        }
        else
        {
            string existingHash = ComputeSha256(destinationDll);
            if (!string.Equals(existingHash, status.PayloadHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The installed xinput_other.dll changed after verification. Nothing was overwritten.");
            }
        }

        bool iniCreated = false;
        string installedIniHash = string.Empty;
        if (File.Exists(payloadIni) && !File.Exists(destinationIni))
        {
            CopyFileAtomic(payloadIni, destinationIni);
            iniCreated = true;
            installedIniHash = ComputeSha256(destinationIni);
        }

        HealthScaleInstallReceipt receipt = new()
        {
            SchemaVersion = 1,
            CompanionVersion = CompanionVersion,
            GameBinPath = status.GameBinPath,
            InstalledDllPath = destinationDll,
            InstalledDllHash = ComputeSha256(destinationDll),
            InstalledUtc = DateTimeOffset.UtcNow,
            ConfigurationCreatedByManager = iniCreated,
            InstalledConfigurationHash = installedIniHash
        };
        SaveJsonAtomic(_receiptPath, receipt);
        return Refresh();
    }

    public HealthScaleUninstallResult Uninstall()
    {
        if (IsGameRunning())
        {
            throw new InvalidOperationException("Close DBXV2 before uninstalling HealthScale.");
        }

        HealthScaleInstallReceipt receipt = LoadJson<HealthScaleInstallReceipt>(_receiptPath)
            ?? throw new InvalidOperationException("No managed HealthScale installation receipt exists. PowerScaler Labs will not remove an unmanaged DLL.");
        string normalizedPath = NormalizeGameBinPath(_settings.GameBinPath)
            ?? throw new InvalidOperationException("The configured DBXV2 location is no longer valid.");
        if (!string.Equals(Path.GetFullPath(receipt.GameBinPath), normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The managed receipt belongs to another DBXV2 folder. Select that installation before uninstalling.");
        }

        string installedDll = Path.Combine(normalizedPath, RuntimeFileName);
        if (!File.Exists(installedDll))
        {
            DeleteReceipt();
            return new HealthScaleUninstallResult(false, File.Exists(Path.Combine(normalizedPath, ConfigurationFileName)));
        }

        string installedHash = ComputeSha256(installedDll);
        if (!string.Equals(installedHash, receipt.InstalledDllHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The installed DLL no longer matches the managed receipt. It was not removed.");
        }

        File.Delete(installedDll);

        bool configurationRemoved = false;
        bool configurationPreserved = false;
        string installedIni = Path.Combine(normalizedPath, ConfigurationFileName);
        if (File.Exists(installedIni))
        {
            if (receipt.ConfigurationCreatedByManager &&
                !string.IsNullOrWhiteSpace(receipt.InstalledConfigurationHash) &&
                string.Equals(ComputeSha256(installedIni), receipt.InstalledConfigurationHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(installedIni);
                configurationRemoved = true;
            }
            else
            {
                configurationPreserved = true;
            }
        }

        DeleteReceipt();
        return new HealthScaleUninstallResult(configurationRemoved, configurationPreserved);
    }

    private static HealthScaleCompanionStatus CreateStatus(
        HealthScaleCompanionState state,
        string detail,
        string gameBinPath,
        string payloadPath,
        string installedPath,
        string payloadHash,
        string installedHash,
        bool gameRunning,
        bool managedInstallation,
        bool canInstall,
        bool canUninstall,
        bool canVerify) =>
        new(
            state,
            StateText(state),
            detail,
            gameBinPath,
            payloadPath,
            installedPath,
            payloadHash,
            installedHash,
            gameRunning,
            managedInstallation,
            canInstall,
            canUninstall,
            canVerify);

    private static string StateText(HealthScaleCompanionState state) => state switch
    {
        HealthScaleCompanionState.NotConfigured => "Location required",
        HealthScaleCompanionState.InvalidGameFolder => "Game folder unavailable",
        HealthScaleCompanionState.PayloadUnavailable => "Payload unavailable",
        HealthScaleCompanionState.NotInstalled => "Not installed",
        HealthScaleCompanionState.InstalledVerified => "Installed · Verified",
        HealthScaleCompanionState.InstalledUnmanaged => "Installed · Not managed",
        HealthScaleCompanionState.Conflict => "Existing DLL conflict",
        _ => "Error"
    };

    private string TryDetectGameBinPath()
    {
        foreach (Process process in Process.GetProcessesByName("DBXV2"))
        {
            using (process)
            {
                try
                {
                    string? executablePath = process.MainModule?.FileName;
                    string? directory = string.IsNullOrWhiteSpace(executablePath) ? null : Path.GetDirectoryName(executablePath);
                    string? normalized = NormalizeGameBinPath(directory ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        return normalized;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Continue to saved/default locations when process details are inaccessible.
                }
            }
        }

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(programFilesX86, "Steam", "steamapps", "common", "DB Xenoverse 2", "bin"),
            Path.Combine(programFiles, "Steam", "steamapps", "common", "DB Xenoverse 2", "bin")
        ];
        return candidates.Select(NormalizeGameBinPath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)) ?? string.Empty;
    }

    private static string? NormalizeGameBinPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            string fullPath = Path.GetFullPath(expanded);
            string[] candidates =
            [
                fullPath,
                Path.Combine(fullPath, "bin")
            ];
            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(candidate, "DBXV2.exe")))
                {
                    return Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }


    private static bool IsSupportedGameVersion(string versionText) =>
        Version.TryParse(versionText, out Version? version) && version == new Version(1, 25, 2, 0);

    private static bool IsGameRunning()
    {
        Process[] processes = Process.GetProcessesByName("DBXV2");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool ReceiptMatches(HealthScaleInstallReceipt? receipt, string gameBinPath, string installedHash)
    {
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.GameBinPath) || string.IsNullOrWhiteSpace(receipt.InstalledDllHash))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(receipt.GameBinPath), Path.GetFullPath(gameBinPath), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(receipt.InstalledDllHash, installedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void CopyFileAtomic(string sourcePath, string destinationPath)
    {
        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            throw new InvalidOperationException("The companion destination directory is invalid.");
        }

        Directory.CreateDirectory(destinationDirectory);
        string temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, temporaryPath, true);
            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static T? LoadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static void SaveJsonAtomic<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The companion state directory is invalid.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporaryPath, path, true);
    }

    private void DeleteReceipt()
    {
        if (File.Exists(_receiptPath))
        {
            File.Delete(_receiptPath);
        }
    }

    private sealed class HealthScaleCompanionSettings
    {
        public string GameBinPath { get; set; } = string.Empty;
    }

    private sealed class HealthScaleInstallReceipt
    {
        public int SchemaVersion { get; set; }
        public string CompanionVersion { get; set; } = string.Empty;
        public string GameBinPath { get; set; } = string.Empty;
        public string InstalledDllPath { get; set; } = string.Empty;
        public string InstalledDllHash { get; set; } = string.Empty;
        public DateTimeOffset InstalledUtc { get; set; }
        public bool ConfigurationCreatedByManager { get; set; }
        public string InstalledConfigurationHash { get; set; } = string.Empty;
    }
}
