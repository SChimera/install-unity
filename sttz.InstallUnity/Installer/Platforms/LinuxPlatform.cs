using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static sttz.InstallUnity.UnityReleaseAPIClient;

namespace sttz.InstallUnity
{
/// <summary>
/// Platform-specific installer code for Linux.
/// </summary>
public class LinuxPlatform : IInstallerPlatform
{
    // -------- Constants --------

    /// <summary>
    /// Base directory where editor content is unpacked initially.
    /// </summary>
    const string INSTALL_DIRECTORY = "/opt"; // Needs root

    /// <summary>
    /// Temporary / default path while installing before versioned move.
    /// </summary>
    const string INSTALL_PATH = "/opt/Unity";

    /// <summary>
    /// Path used to temporarily move an existing installation out of the way.
    /// </summary>
    const string INSTALL_PATH_TMP = "/opt/Unity (Moved by " + UnityInstaller.PRODUCT_NAME + ")";

    // -------- IInstallerPlatform --------

    public Task<(Platform, Architecture)> GetCurrentPlatform()
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        return arch switch {
            System.Runtime.InteropServices.Architecture.X64 => Task.FromResult((Platform.Linux, Architecture.X86_64)),
            System.Runtime.InteropServices.Architecture.Arm64 => Task.FromResult((Platform.Linux, Architecture.ARM64)), // future support
            _ => throw new Exception($"Unsupported Linux architecture: {arch}")
        };
    }

    public async Task<Architecture> GetInstallableArchitectures()
    {
        var (_, arch) = await GetCurrentPlatform();
        if (arch == Architecture.X86_64) return Architecture.X86_64; // x86 only installs that arch
        return Architecture.ARM64 | Architecture.X86_64; // arm64 can install both (Rosetta-like user decisions in future)
    }

    string GetUserDirectory(string xdgName, Environment.SpecialFolder fallbackFolder, string fallbackSubdir)
    {
        var xdg = Environment.GetEnvironmentVariable(xdgName);
        if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, UnityInstaller.PRODUCT_NAME);
        var baseDir = Environment.GetFolderPath(fallbackFolder);
        return Path.Combine(baseDir, fallbackSubdir, UnityInstaller.PRODUCT_NAME);
    }

    public string GetConfigurationDirectory()
    {
        return GetUserDirectory("XDG_CONFIG_HOME", Environment.SpecialFolder.UserProfile, ".config");
    }

    public string GetCacheDirectory()
    {
        return GetUserDirectory("XDG_CACHE_HOME", Environment.SpecialFolder.UserProfile, ".cache");
    }

    public string GetDownloadDirectory()
    {
        return Path.Combine(Path.GetTempPath(), UnityInstaller.PRODUCT_NAME);
    }

    public Task<bool> IsAdmin(CancellationToken cancellation = default)
    {
        return CheckIsRoot(false, cancellation);
    }

    public async Task<bool> PromptForPasswordIfNecessary(CancellationToken cancellation = default)
    {
        if (await CheckIsRoot(false, cancellation)) return true;

        Console.WriteLine();
        var attempts = 3;
        while (true) {
            if (pwd == null) {
                Console.Write($"{UnityInstaller.PRODUCT_NAME} requires your admin password: ");
                pwd = Helpers.ReadPassword();
            }

            if (await CheckIsRoot(true, cancellation)) {
                return true;
            } else if (--attempts > 0) {
                Console.WriteLine("Sorry, try again.");
                pwd = null;
            } else {
                pwd = null;
                return false;
            }
        }
    }

    public async Task<IEnumerable<Installation>> FindInstallations(CancellationToken cancellation = default)
    {
        var installations = new List<Installation>();
        string[] roots = new string[] { "/opt", "/usr/local", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share") };

        foreach (var root in roots) {
            if (!Directory.Exists(root)) continue;
            string[] unityDirs = Array.Empty<string>();
            try {
                unityDirs = Directory.GetDirectories(root, "Unity*");
            } catch { /* ignore */ }
            foreach (var dir in unityDirs) {
                var editorExe = Path.Combine(dir, "Editor", "Unity");
                if (!File.Exists(editorExe)) continue;
                var versionTxt = Path.Combine(dir, "Editor", "Data", "UnityVersion.txt");
                UnityVersion version = default;
                if (File.Exists(versionTxt)) {
                    try {
                        var line = File.ReadLines(versionTxt).FirstOrDefault()?.Trim();
                        if (!string.IsNullOrEmpty(line)) version = new UnityVersion(line);
                    } catch (Exception e) {
                        Logger.LogWarning($"Failed reading version at '{dir}': {e.Message}");
                    }
                }
                if (!version.IsFullVersion) {
                    Logger.LogWarning($"Could not determine Unity version at path '{dir}'");
                    continue;
                }
                installations.Add(new Installation { path = dir, executable = editorExe, version = version });
                Logger.LogDebug($"Found Unity {version} at path: {dir}");
            }
        }
        return installations;
    }

    public async Task PrepareInstall(UnityInstaller.Queue queue, string installationPaths, CancellationToken cancellation = default)
    {
        if (installing.Version.IsValid)
            throw new InvalidOperationException($"Already installing another version: {installing.Version}");

        installing = queue.metadata;
        this.installationPaths = installationPaths;
        installedEditor = false;

        // Upgrade detection (not installing editor but modules)
        upgradeOriginalPath = null;
        if (!queue.items.Any(i => i.package is EditorDownload)) {
            var installs = await FindInstallations(cancellation);
            var existingInstall = installs.FirstOrDefault(i => i.version == queue.metadata.Version);
            if (existingInstall == null) {
                throw new InvalidOperationException($"Not installing editor but version {queue.metadata.Version} not already installed.");
            }
            upgradeOriginalPath = existingInstall.path;
        }

        // Move existing installation at default path out of the way if needed
        movedExisting = false;
        if (upgradeOriginalPath != INSTALL_PATH) {
            if (Directory.Exists(INSTALL_PATH)) {
                if (Directory.Exists(INSTALL_PATH_TMP)) {
                    throw new InvalidOperationException($"Fallback installation path '{INSTALL_PATH_TMP}' already exists.");
                }
                Logger.LogInformation("Temporarily moving existing installation at default install path: " + INSTALL_PATH);
                await Move(INSTALL_PATH, INSTALL_PATH_TMP, cancellation);
                movedExisting = true;
            }
            if (upgradeOriginalPath != null) {
                Logger.LogInformation($"Temporarily moving installation to upgrade from '{upgradeOriginalPath}' to default install path");
                await Move(upgradeOriginalPath, INSTALL_PATH, cancellation);
            }
        }
    }

    public async Task Install(UnityInstaller.Queue queue, UnityInstaller.QueueItem item, CancellationToken cancellation = default)
    {
        if (item.package is not EditorDownload && !installedEditor && upgradeOriginalPath == null) {
            throw new InvalidOperationException("Cannot install package without installing editor first.");
        }

        var module = (item.package as Module);
        var extension = Path.GetFileName(item.filePath).ToLowerInvariant();

        if (item.package is EditorDownload) {
            if (!(extension.EndsWith(".tar.xz") || extension.EndsWith(".tar.gz")))
                throw new Exception($"Unexpected file type for editor package (expected .tar.xz or .tar.gz but got '{Path.GetFileName(item.filePath)}')");
            await InstallTar(item.filePath, INSTALL_PATH, stripToUnityRoot: true, cancellation);
        } else {
            switch (true) {
                case bool _ when extension.EndsWith(".tar.xz") || extension.EndsWith(".tar.gz"):
                    var dest = string.IsNullOrEmpty(module.destination) ? INSTALL_PATH : module.destination.Replace("{UNITY_PATH}", INSTALL_PATH);
                    await InstallTar(item.filePath, dest, stripToUnityRoot: false, cancellation);
                    break;
                case bool _ when extension.EndsWith(".zip"):
                    await InstallZip(item.filePath, module.destination, cancellation);
                    break;
                case bool _ when extension.EndsWith(".po"):
                    await InstallFile(item.filePath, module.destination, cancellation);
                    break;
                case bool _ when extension.EndsWith(".sh"):
                    await InstallShell(item.filePath, module.destination, cancellation);
                    break;
                default:
                    throw new Exception("Cannot install package of type: " + module.type + " (" + Path.GetFileName(item.filePath) + ")");
            }
        }

        if (module?.extractedPathRename.IsSet == true) {
            await Rename(item.filePath, module.extractedPathRename, cancellation);
        }

        if (item.package is EditorDownload) installedEditor = true;
    }

    public async Task<Installation> CompleteInstall(bool aborted, CancellationToken cancellation = default)
    {
        if (!installing.Version.IsValid)
            throw new InvalidOperationException("Not installing any version to complete");

        string destination = null;
        if (upgradeOriginalPath != null) {
            destination = upgradeOriginalPath;
            if (upgradeOriginalPath != INSTALL_PATH) {
                Logger.LogInformation("Moving back upgraded installation to: " + destination);
                await Move(INSTALL_PATH, destination, cancellation);
            }
        } else if (!aborted) {
            destination = GetUniqueInstallationPath(installing.Version, installationPaths);
            Logger.LogInformation("Moving newly installed version to: " + destination);
            await Move(INSTALL_PATH, destination, cancellation);
        } else if (aborted) {
            Logger.LogInformation("Deleting aborted installation at path: " + INSTALL_PATH);
            await Delete(INSTALL_PATH, cancellation);
        }

        if (movedExisting) {
            Logger.LogInformation("Moving back installation that was at default installation path");
            await Move(INSTALL_PATH_TMP, INSTALL_PATH, cancellation);
        }

        if (!aborted) {
            var executable = Path.Combine(destination, "Editor", "Unity");
            if (!File.Exists(executable)) {
                Logger.LogError("Could not find Unity executable at path: " + executable);
                return default;
            }
            var installation = new Installation() {
                version = installing.Version,
                executable = executable,
                path = destination
            };
            installing = default;
            movedExisting = false;
            upgradeOriginalPath = null;
            return installation;
        }
        return default;
    }

    public async Task MoveInstallation(Installation installation, string newPath, CancellationToken cancellation = default)
    {
        if (Directory.Exists(newPath) || File.Exists(newPath))
            throw new ArgumentException("Destination path already exists: " + newPath);
        await Move(installation.path, newPath, cancellation);
        installation.path = newPath;
    }

    public async Task Uninstall(Installation installation, CancellationToken cancellation = default)
    {
        await Delete(installation.path, cancellation);
    }

    public async Task Run(Installation installation, IEnumerable<string> arguments, bool child)
    {
        var exe = installation.executable;
        if (!File.Exists(exe)) throw new FileNotFoundException("Unity executable not found", exe);
        if (!child) {
            var cmd = new System.Diagnostics.Process();
            cmd.StartInfo.FileName = exe;
            cmd.StartInfo.Arguments = string.Join(" ", arguments);
            Logger.LogInformation($"$ {cmd.StartInfo.FileName} {cmd.StartInfo.Arguments}");
            cmd.Start();
            while (!cmd.HasExited) await Task.Delay(100);
        } else {
            if (!arguments.Contains("-logFile")) arguments = arguments.Append("-logFile").Append("-");
            var cmd = new System.Diagnostics.Process();
            cmd.StartInfo.FileName = exe;
            cmd.StartInfo.Arguments = string.Join(" ", arguments);
            cmd.StartInfo.UseShellExecute = false;
            cmd.StartInfo.RedirectStandardOutput = true;
            cmd.StartInfo.RedirectStandardError = true;
            cmd.EnableRaisingEvents = true;
            cmd.OutputDataReceived += (s, a) => { if (a.Data != null) Logger.LogInformation(a.Data); };
            cmd.ErrorDataReceived += (s, a) => { if (a.Data != null) Logger.LogError(a.Data); };
            cmd.Start();
            cmd.BeginOutputReadLine();
            cmd.BeginErrorReadLine();
            while (!cmd.HasExited) await Task.Delay(100);
            cmd.WaitForExit();
            Logger.LogInformation($"Unity exited with code {cmd.ExitCode}");
            Environment.Exit(cmd.ExitCode);
        }
    }

    // -------- Helpers --------

    ILogger Logger = UnityInstaller.CreateLogger<LinuxPlatform>();

    bool? isRoot;
    string pwd;
    VersionMetadata installing;
    string installationPaths;
    string upgradeOriginalPath;
    bool movedExisting;
    bool installedEditor;

    string GetUniqueInstallationPath(UnityVersion version, string installationPaths)
    {
        string expanded = null;
        if (!string.IsNullOrEmpty(installationPaths)) {
            var comparison = StringComparison.OrdinalIgnoreCase;
            var paths = installationPaths.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths) {
                expanded = path.Trim();
                expanded = Helpers.Replace(expanded, "{major}", version.major.ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{minor}", version.minor.ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{patch}", version.patch.ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{type}", ((char)version.type).ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{build}", version.build.ToString(), comparison);
                expanded = Helpers.Replace(expanded, "{hash}", version.hash, comparison);
                if (!Directory.Exists(expanded)) return expanded;
            }
        }
        if (expanded != null) return Helpers.GenerateUniqueFileName(expanded); else return Helpers.GenerateUniqueFileName(INSTALL_PATH);
    }

    async Task InstallTar(string filePath, string destination, bool stripToUnityRoot, CancellationToken cancellation)
    {
        var targetDir = destination.Replace("{UNITY_PATH}", INSTALL_PATH);
        var retryWithRoot = false;
        try {
            Directory.CreateDirectory(targetDir);
            await RunTarExtract(filePath, targetDir, stripToUnityRoot, useSudo:false, cancellation);
        } catch (Exception e) {
            Logger.LogInformation($"Tar extract as user failed, trying as root... ({e.Message})");
            retryWithRoot = true;
        }
        if (retryWithRoot) {
            var result = await Sudo("/bin/mkdir", $"-p \"{targetDir}\"", cancellation);
            if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
            await RunTarExtract(filePath, targetDir, stripToUnityRoot, useSudo:true, cancellation);
        }
        // Ensure permissions readable
        await Sudo("/bin/chmod", $"-R o+rX \"{targetDir}\"", cancellation);
    }

    async Task RunTarExtract(string filePath, string targetDir, bool stripToUnityRoot, bool useSudo, CancellationToken cancellation)
    {
        var cmd = useSudo ? (Func<string,string,CancellationToken,Task<(int,string,string)>>)((c,a,ct)=>Sudo(c,a,ct)) : (c,a,ct)=>Command.Run(c,a,cancellation:ct);
        if (stripToUnityRoot) {
            var tempRoot = Path.Combine(Path.GetTempPath(), UnityInstaller.PRODUCT_NAME, Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(filePath)));
            Directory.CreateDirectory(tempRoot);
            var (exitCode, _, error) = await cmd("/bin/tar", $"-xf \"{filePath}\" -C \"{tempRoot}\"", cancellation);
            if (exitCode != 0) throw new Exception($"ERROR: {error}");
            var candidates = Directory.GetDirectories(tempRoot, "*", SearchOption.AllDirectories)
                .Where(d => File.Exists(Path.Combine(d, "Editor", "Unity")))
                .OrderBy(d => d.Length).ToList();
            string sourceRoot = null;
            if (candidates.Count > 0) sourceRoot = candidates.First(); else if (File.Exists(Path.Combine(tempRoot, "Editor", "Unity"))) sourceRoot = tempRoot;
            if (sourceRoot == null) throw new Exception("Failed to locate Unity root inside archive");
            foreach (var dir in Directory.GetDirectories(sourceRoot)) {
                var dst = Path.Combine(targetDir, Path.GetFileName(dir));
                if (Directory.Exists(dst)) await Delete(dst, cancellation);
                await Copy(dir, dst, cancellation);
            }
            foreach (var file in Directory.GetFiles(sourceRoot)) {
                var dst = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, dst, true);
            }
        } else {
            var (exitCode, _, error) = await cmd("/bin/tar", $"-xf \"{filePath}\" -C \"{targetDir}\"", cancellation);
            if (exitCode != 0) throw new Exception($"ERROR: {error}");
        }
    }

    async Task InstallFile(string filePath, string destination, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(destination)) throw new Exception($"Cannot install {filePath}: File packages must have a destination set.");
        var targetDir = destination.Replace("{UNITY_PATH}", INSTALL_PATH);
        var dst = Path.Combine(targetDir, Path.GetFileName(filePath));
        await Copy(filePath, dst, cancellation);
    }

    async Task InstallZip(string filePath, string destination, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(destination)) throw new Exception($"Cannot install {filePath}: Zip packages must have a destination set.");
        var target = destination.Replace("{UNITY_PATH}", INSTALL_PATH);
        var retryWithRoot = false;
        (int exitCode, string output, string error) result;
        try {
            Directory.CreateDirectory(target);
            result = await Command.Run("/usr/bin/unzip", $"-o -d \"{target}\" \"{filePath}\"", cancellation: cancellation);
            if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
        } catch (Exception e) {
            Logger.LogInformation($"Unzip as user failed, trying as root... ({e.Message})");
            retryWithRoot = true;
        }
        if (retryWithRoot) {
            result = await Sudo("/bin/mkdir", $"-p \"{target}\"", cancellation);
            if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
            result = await Sudo("/usr/bin/unzip", $"-o -d \"{target}\" \"{filePath}\"", cancellation);
            if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
        }
        await Sudo("/bin/chmod", $"-R o+rX \"{target}\"", cancellation);
    }

    async Task InstallShell(string filePath, string destination, CancellationToken cancellation)
    {
        if (string.IsNullOrEmpty(destination)) throw new Exception($"Cannot install {filePath}: Shell script packages must have a destination set.");
        var targetDir = destination.Replace("{UNITY_PATH}", INSTALL_PATH);
        Directory.CreateDirectory(targetDir);
        var dst = Path.Combine(targetDir, Path.GetFileName(filePath));
        File.Copy(filePath, dst, true);
        await Sudo("/bin/chmod", $"+x \"{dst}\"", cancellation);
        // Some shell packages might require execution to self-extract
        // We only run if looks like a self-extract (heuristic: contains 'tar -xf' in first 2000 chars)
        var head = File.ReadAllText(dst, System.Text.Encoding.UTF8);
        if (head.Contains("tar -xf") || head.Contains("#!/")) {
            var result = await Sudo("/bin/bash", $"\"{dst}\" --quiet", cancellation);
            if (result.exitCode != 0) Logger.LogWarning($"Shell package returned code {result.exitCode}: {result.error}");
        }
    }

    async Task Rename(string filePath, PathRename rename, CancellationToken cancellation)
    {
        var from = rename.from.Replace("{UNITY_PATH}", INSTALL_PATH);
        var to = rename.to.Replace("{UNITY_PATH}", INSTALL_PATH);
        if (!Directory.Exists(from) && !File.Exists(from)) throw new Exception($"{filePath}: renameFrom path does not exist: {from}");
        await Move(from, to, cancellation);
    }

    async Task Move(string sourcePath, string newPath, CancellationToken cancellation)
    {
        if (sourcePath.StartsWith(newPath + "/")) {
            var tmpSource = Path.Combine(Path.GetTempPath(), UnityInstaller.PRODUCT_NAME, Path.GetFileName(newPath));
            await Move(sourcePath, tmpSource, cancellation);
            await Delete(newPath, cancellation);
            sourcePath = tmpSource;
        }
        var baseDst = Path.GetDirectoryName(newPath);
        try {
            Directory.CreateDirectory(baseDst);
            Directory.Move(sourcePath, newPath);
            return;
        } catch (Exception e) {
            Logger.LogInformation($"Move as user failed, trying as root... ({e.Message})");
        }
        var result = await Sudo("/bin/mkdir", $"-p \"{baseDst}\"", cancellation);
        if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
        result = await Sudo("/bin/mv", $"\"{sourcePath}\" \"{newPath}\"", cancellation);
        if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
    }

    async Task Copy(string sourcePath, string newPath, CancellationToken cancellation)
    {
        var baseDst = Path.GetDirectoryName(newPath);
        (int exitCode, string output, string error) result;
        try {
            result = await Command.Run("/bin/mkdir", $"-p \"{baseDst}\"", cancellation: cancellation);
            if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
            if (Directory.Exists(sourcePath)) {
                result = await Command.Run("/bin/cp", $"-a \"{sourcePath}\" \"{newPath}\"", cancellation: cancellation);
            } else {
                result = await Command.Run("/bin/cp", $"-a \"{sourcePath}\" \"{newPath}\"", cancellation: cancellation);
            }
            if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
            return;
        } catch (Exception e) {
            Logger.LogInformation($"Copy as user failed, trying as root... ({e.Message})");
        }
        result = await Sudo("/bin/mkdir", $"-p \"{baseDst}\"", cancellation);
        if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
        result = await Sudo("/bin/cp", $"-a \"{sourcePath}\" \"{newPath}\"", cancellation);
        if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
    }

    async Task Delete(string deletePath, CancellationToken cancellation = default)
    {
        try {
            Directory.Delete(deletePath, true);
            return;
        } catch (Exception e) {
            Logger.LogInformation($"Deleting as user failed, trying as root... ({e.Message})");
        }
        var result = await Sudo("/bin/rm", $"-rf \"{deletePath}\"", cancellation);
        if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
    }

    async Task<bool> CheckIsRoot(bool withSudo, CancellationToken cancellation)
    {
        var command = "/usr/bin/id";
        var arguments = "-u";
        (int exitCode, string output, string error) result;
        if (withSudo) {
            result = await Sudo(command, arguments, cancellation: cancellation);
            if (result.exitCode != 0) {
                if (result.exitCode == 1 && result.error.Contains("Sorry, try again.")) return false;
                throw new Exception($"ERROR: {result.error}");
            }
        } else {
            result = await Command.Run(command, arguments, cancellation: cancellation);
            if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
        }
        if (!int.TryParse(result.output, out var id)) throw new Exception($"ERROR: failed to run id, cannot parse output: {result.output} / {result.error}");
        return id == 0;
    }

    async Task<(int exitCode, string output, string error)> Sudo(string command, string arguments, CancellationToken cancellation)
    {
        if (isRoot == null) isRoot = await CheckIsRoot(false, cancellation);
        if (isRoot == true) return await Command.Run(command, arguments, cancellation: cancellation);
        if (pwd == null) await PromptForPasswordIfNecessary(cancellation);
        return await Command.Run("sudo", "-Sk " + command + " " + arguments, pwd + "\n", cancellation);
    }
}
}
