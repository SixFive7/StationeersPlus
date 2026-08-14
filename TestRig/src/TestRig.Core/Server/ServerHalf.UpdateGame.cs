using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Server;

public sealed partial class ServerHalf
{
    /// <summary>The Steam app id of the Stationeers dedicated server.</summary>
    public const int SteamAppId = 600760;

    /// <summary>
    /// Loader files copied out of the client install beside the mirrored BepInEx tree.
    /// </summary>
    public static readonly IReadOnlyList<string> LoaderFiles =
        ["winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt"];

    /// <summary>
    /// Where the StationeersLaunchPad server zip is cached, keyed by version.
    /// </summary>
    /// <remarks>
    /// Inside the rig, not under <c>.work/</c> (SERVER-021 fixed). The repository rule is
    /// that everything under <c>.work/</c> lives in a dated session folder, with exactly one
    /// exception for the version-keyed decompile cache, and the PowerShell wrote a
    /// permanent, undated <c>.work/launchpad-server/</c>. This cache belongs to the rig, is
    /// keyed by version so staleness is visible, and sits inside the deny-all gitignore.
    /// </remarks>
    public string LaunchPadCacheDir(string version) =>
        Path.Combine(_paths.DataDir, "cache", "launchpad-server", version);

    /// <summary>
    /// Installs or refreshes the dedicated server: SteamCMD, then the BepInEx mirror.
    /// </summary>
    /// <remarks>
    /// Named for the concept and not for the mechanism. It used to be <c>-Bootstrap</c> here
    /// and <c>-Provision -Force</c> on the client half, which is how one agent asked to
    /// "update the testrig" updated exactly one half and had no way to notice.
    /// </remarks>
    public void UpdateGame(string? callerId = null)
    {
        AssertGate("update-game", callerId);

        // SERVER-028 fixed: the PowerShell had NO running-server guard here, unlike deploy and
        // update-mods. Run against a live server it fails part way with sharing violations and
        // leaves a HALF-MIRRORED BepInEx tree, which is a worse state than either guarded verb
        // would produce, and the next start loads whatever survived.
        if (ServerAlive || WrapperAlive)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"The dedicated server is running (host PID {HostPid}, server PID {ServerPid}). update-game "
                + "replaces the whole BepInEx tree and the game binaries, and Mono holds an exclusive lock on "
                + "every loaded plugin DLL on Windows: a mirror over a live server fails part way and leaves a "
                + "half-written tree the next start loads anyway. Run: testrig stop --target server --as <id>");
        }

        Say("[UpdateGame] Verifying environment...");
        var stationeers = _env.StationeersPath();
        var steamcmd = _env.SteamcmdPath();
        Say($"[UpdateGame]   StationeersPath: {stationeers}");
        Say($"[UpdateGame]   STEAMCMD_PATH:   {steamcmd}");
        Say($"[UpdateGame]   Server install:  {_paths.InstallDir}");
        Say($"[UpdateGame]   Server data:     {_paths.DataDir}");

        foreach (var dir in new[] { _paths.InstallDir, _paths.DataDir }) _fs.CreateDirectory(dir);

        Say($"[UpdateGame] Running SteamCMD (app {SteamAppId})...");
        var exit = _steamcmd.Run(steamcmd,
        [
            "+force_install_dir", _paths.InstallDir,
            "+login", "anonymous",
            "+app_update", SteamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture), "validate",
            "+quit",
        ]);

        if (exit != 0)
        {
            throw new RigRefusalException(RigRefusalKind.Refused, $"SteamCMD failed with exit code {exit}.");
        }
        if (!_fs.FileExists(_paths.Exe))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                "update-game: rocketstation_DedicatedServer.exe missing after the SteamCMD run.");
        }
        Say("[UpdateGame] SteamCMD install complete.");

        MirrorBepInEx(stationeers);
        OverlayLaunchPadServerZip();

        Say("[UpdateGame] Done. Next: testrig update-mods --target server, then testrig deploy, then "
            + "testrig start --target server.");
    }

    /// <summary>
    /// Replaces the server's BepInEx tree with the client install's.
    /// </summary>
    /// <remarks>
    /// The server install ships no loader at all, so the client's is mirrored whole and the
    /// four loose loader files are copied beside it.
    /// </remarks>
    private void MirrorBepInEx(string stationeers)
    {
        Say("[UpdateGame] Mirroring BepInEx tree from client install...");

        var source = Path.Combine(stationeers, "BepInEx");
        if (!_fs.DirectoryExists(source))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Client BepInEx not found at {source}. Install StationeersLaunchPad on the client first.");
        }

        if (_fs.DirectoryExists(_paths.BepInEx)) _fs.DeleteDirectory(_paths.BepInEx, recursive: true);
        TreeOps.CopyTree(_fs, source, _paths.BepInEx);

        foreach (var leaf in LoaderFiles)
        {
            var file = Path.Combine(stationeers, leaf);
            if (_fs.FileExists(file)) _fs.CopyFile(file, Path.Combine(_paths.InstallDir, leaf), overwrite: true);
        }

        if (_fs.FileExists(_paths.BepInExCoreDll))
        {
            Say($"[UpdateGame] BepInEx mirrored from {source}.");
        }
    }

    /// <summary>
    /// Overlays the StationeersLaunchPad server-zip release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists for <c>RG.ImGui.dll</c>, which is in the server zip and not in the client
    /// install. Every other DLL is byte-identical, so the overlay is a no-op for them.
    /// </para>
    /// <para>
    /// <b>The download goes to a temp name and is verified before it is moved into the cache
    /// (SERVER-022 fixed).</b> The PowerShell wrote straight to the final cache path, so a
    /// partial or zero-byte file poisoned every later run: the next update-game saw the file,
    /// skipped the download, and called the extractor on a corrupt archive, which threw
    /// uncaught AFTER SteamCMD had already replaced the BepInEx tree.
    /// </para>
    /// </remarks>
    private void OverlayLaunchPadServerZip()
    {
        Say("[UpdateGame] Overlaying StationeersLaunchPad server-zip release...");

        if (!_fs.FileExists(_paths.LaunchPadDll))
        {
            Warn($"[UpdateGame] StationeersLaunchPad.dll not found at {_paths.LaunchPadDll}; skipping the "
                 + "server-zip overlay. Mods will not load until StationeersLaunchPad is installed.");
            return;
        }

        var version = LaunchPadVersion();
        if (string.IsNullOrEmpty(version))
        {
            Warn("[UpdateGame] Could not read a version from the mirrored StationeersLaunchPad.dll; skipping "
                 + "the server-zip overlay. Mod loading may be missing RG.ImGui.");
            return;
        }

        var url = $"https://github.com/StationeersLaunchPad/StationeersLaunchPad/releases/download/"
                  + $"v{version}/StationeersLaunchPad-server-v{version}.zip";

        var cacheDir = LaunchPadCacheDir(version);
        var zipPath = Path.Combine(cacheDir, $"StationeersLaunchPad-server-v{version}.zip");
        var extractDir = Path.Combine(cacheDir, "extracted");

        _fs.CreateDirectory(cacheDir);

        if (!_fs.FileExists(zipPath))
        {
            Say($"[UpdateGame]   downloading {url}");

            var temp = zipPath + ".partial";
            try
            {
                _fs.DeleteFile(temp);
                _downloader.Download(url, temp);

                // Verified before it is allowed to become the cached copy. A zero-byte or
                // truncated file is exactly what poisons the skip-the-download branch.
                if (!_fs.FileExists(temp) || _fs.GetFileLength(temp) <= 0)
                {
                    throw new IOException("the download produced no bytes");
                }

                TreeOps.MoveFile(_fs, temp, zipPath);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException
                                           or InvalidOperationException or TaskCanceledException)
            {
                _fs.DeleteFile(temp);
                Warn($"[UpdateGame]   download failed: {ex.Message}. Skipping overlay; mod loading may be "
                     + "missing RG.ImGui.");
                return;
            }
        }

        try
        {
            if (_fs.DirectoryExists(extractDir)) _fs.DeleteDirectory(extractDir, recursive: true);
            _extractor.Extract(zipPath, extractDir);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A cached archive that will not open is a cached archive that is wrong. Removing
            // it makes the next run re-download instead of failing the same way for ever.
            _fs.DeleteFile(zipPath);
            Warn($"[UpdateGame]   the cached server zip at {zipPath} could not be expanded ({ex.Message}); it "
                 + "has been deleted so the next update-game downloads it again. Skipping overlay; mod loading "
                 + "may be missing RG.ImGui.");
            return;
        }

        var sourceDir = Path.Combine(extractDir, "StationeersLaunchPad");
        if (!_fs.DirectoryExists(sourceDir))
        {
            Warn($"[UpdateGame]   the server zip has no StationeersLaunchPad folder at {sourceDir}; skipping "
                 + "the overlay.");
            return;
        }

        var destination = Path.GetDirectoryName(_paths.LaunchPadDll)!;
        var copied = 0;
        foreach (var file in _fs.EnumerateFiles(sourceDir, "*", recurse: false))
        {
            _fs.CopyFile(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            copied++;
        }

        Say($"[UpdateGame]   overlaid {copied} files from the server zip into {destination}");
    }

    /// <summary>
    /// The mirrored StationeersLaunchPad version, which selects the server-zip release.
    /// </summary>
    /// <remarks>
    /// Read from the plugin's own <c>version.txt</c> sidecar when it has one, and otherwise
    /// from a <c>StationeersLaunchPad-&lt;version&gt;</c> marker beside it. A file version
    /// cannot be read through the filesystem seam, and reaching around the seam to
    /// <c>FileVersionInfo</c> would put an untestable call in the middle of the one verb that
    /// rewrites the whole install. Returning empty degrades to a named warning rather than a
    /// wrong URL.
    /// </remarks>
    public string LaunchPadVersion()
    {
        var dir = Path.GetDirectoryName(_paths.LaunchPadDll);
        if (string.IsNullOrEmpty(dir) || !_fs.DirectoryExists(dir)) return "";

        var marker = Path.Combine(dir, "version.txt");
        if (_fs.FileExists(marker))
        {
            try
            {
                var text = _fs.ReadAllText(marker).Trim();
                var match = System.Text.RegularExpressions.Regex.Match(text, @"\d+(?:\.\d+)+");
                if (match.Success) return match.Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "";
            }
        }

        foreach (var file in _fs.EnumerateFiles(dir, "StationeersLaunchPad-*", recurse: false))
        {
            var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"\d+(?:\.\d+)+");
            if (match.Success) return match.Value;
        }

        return "";
    }
}
