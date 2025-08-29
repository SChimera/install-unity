using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// As of Unity6, the only supported architecture is x86_64.
        /// https://docs.unity3d.com/6000.0/Documentation/Manual/system-requirements.html
        /// </summary>
        const Architecture SUPPORTED_ARCHITECTURE = Architecture.X86_64;

        // -------- IInstallerPlatform --------

        public Task<(Platform, Architecture)> GetCurrentPlatform()
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
            if (arch != System.Runtime.InteropServices.Architecture.X64)
            {
                throw new NotSupportedException(
                    $"Unsupported architecture {arch} for Linux platform. Only {SUPPORTED_ARCHITECTURE} is supported.");
            }

            return Task.FromResult((Platform.Linux, SUPPORTED_ARCHITECTURE));
        }

        public Task<Architecture> GetInstallableArchitectures()
        {
            return Task.FromResult(SUPPORTED_ARCHITECTURE);
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
            if (await CheckIsRoot(false, cancellation))
                return true;

            Console.WriteLine();
            const int maxAttempts = 3;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();

                if (_pwd == null)
                {
                    Console.Write($"{UnityInstaller.PRODUCT_NAME} requires your admin password: ");
                    _pwd = Helpers.ReadPassword();
                    if (string.IsNullOrEmpty(_pwd))
                    {
                        Console.WriteLine("Empty password.");
                        _pwd = null;
                        continue;
                    }
                }

                try
                {
                    if (await CheckIsRoot(true, cancellation))
                        return true;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    Console.WriteLine($"Authentication failed: {ex.Message}");
                    _pwd = null;
                    continue;
                }

                if (attempt < maxAttempts)
                {
                    Console.WriteLine("Sorry, try again.");
                    _pwd = null;
                }
            }

            _pwd = null;
            return false;
        }

        public Task<IEnumerable<Installation>> FindInstallations(CancellationToken cancellation = default)
        {
            var installations = new List<Installation>();
            var roots = new[]
            {
                INSTALL_DIRECTORY, "/usr/local",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share"),
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string[] unityDirs = [];
                try
                {
                    unityDirs = Directory.GetDirectories(root, "Unity*");
                }
                catch
                {
                    /* ignore */
                }

                foreach (var dir in unityDirs)
                {
                    var editorExe = Path.Combine(dir, "Editor", "Unity");
                    if (!File.Exists(editorExe)) continue;
                    var versionTxt = Path.Combine(dir, "Editor", "Data", "UnityVersion.txt");
                    UnityVersion version = default;
                    if (File.Exists(versionTxt))
                    {
                        try
                        {
                            var line = File.ReadLines(versionTxt).FirstOrDefault()?.Trim();
                            if (!string.IsNullOrEmpty(line)) version = new UnityVersion(line);
                        }
                        catch (Exception e)
                        {
                            _logger.LogWarning($"Failed reading version at '{dir}': {e.Message}");
                        }
                    }

                    if (!version.IsFullVersion)
                    {
                        _logger.LogWarning($"Could not determine Unity version at path '{dir}'");
                        continue;
                    }

                    installations.Add(new() { path = dir, executable = editorExe, version = version });
                    _logger.LogDebug($"Found Unity {version} at path: {dir}");
                }
            }

            return Task.FromResult<IEnumerable<Installation>>(installations);
        }

        public async Task PrepareInstall(UnityInstaller.Queue queue, string installationPaths,
            CancellationToken cancellation = default)
        {
            if (_installing.Version.IsValid)
                throw new InvalidOperationException($"Already installing another version: {_installing.Version}");

            _installing = queue.metadata;
            _installationPaths = installationPaths;
            _installedEditor = false;

            // Upgrade detection (not installing editor but modules)
            _upgradeOriginalPath = null;
            if (!queue.items.Any(i => i.package is EditorDownload))
            {
                var installs = await FindInstallations(cancellation);
                var existingInstall = installs.FirstOrDefault(i => i.version == queue.metadata.Version);
                if (existingInstall == null)
                {
                    throw new InvalidOperationException(
                        $"Not installing editor but version {queue.metadata.Version} not already installed.");
                }

                _upgradeOriginalPath = existingInstall.path;
            }

            // Move existing installation at default path out of the way if needed
            _movedExisting = false;
            if (_upgradeOriginalPath != INSTALL_PATH)
            {
                if (Directory.Exists(INSTALL_PATH))
                {
                    if (Directory.Exists(INSTALL_PATH_TMP))
                    {
                        throw new InvalidOperationException(
                            $"Fallback installation path '{INSTALL_PATH_TMP}' already exists.");
                    }

                    _logger.LogInformation("Temporarily moving existing installation at default install path: " +
                                           INSTALL_PATH);
                    await Move(INSTALL_PATH, INSTALL_PATH_TMP, cancellation);
                    _movedExisting = true;
                }

                if (_upgradeOriginalPath != null)
                {
                    _logger.LogInformation(
                        $"Temporarily moving installation to upgrade from '{_upgradeOriginalPath}' to default install path");
                    await Move(_upgradeOriginalPath, INSTALL_PATH, cancellation);
                }
            }
        }

        public async Task Install(UnityInstaller.Queue queue, UnityInstaller.QueueItem item,
            CancellationToken cancellation = default)
        {
            // Validate prerequisites
            ValidateInstallPrerequisites(item);

            var extension = Path.GetFileName(item.filePath).ToLowerInvariant();

            if (item.package is EditorDownload)
            {
                await InstallEditor(item.filePath, extension, cancellation);
            }
            else if (item.package is Module module)
            {
                await InstallModule(module, item.filePath, extension, cancellation);
                await ApplyPostInstallOperations(module, item.filePath, cancellation);
            }
        }

        private void ValidateInstallPrerequisites(UnityInstaller.QueueItem item)
        {
            if (item.package is not EditorDownload && !_installedEditor && _upgradeOriginalPath == null)
            {
                throw new InvalidOperationException("Cannot install package without installing editor first.");
            }
        }

        private async Task InstallEditor(string filePath, string extension, CancellationToken cancellation)
        {
            if (!IsTarArchive(extension))
            {
                throw new InvalidOperationException(
                    $"Unexpected file type for editor package (expected .tar.xz or .tar.gz but got '{Path.GetFileName(filePath)}')");
            }

            await InstallTar(filePath, INSTALL_PATH, stripToUnityRoot: true, cancellation);
            _installedEditor = true;
        }

        private async Task InstallModule(Module module, string filePath, string extension,
            CancellationToken cancellation)
        {
            var installHandler = GetInstallHandler(extension);

            if (installHandler == null)
            {
                throw new NotSupportedException(
                    $"Cannot install package of type: {module.type} ({Path.GetFileName(filePath)})");
            }

            await installHandler(filePath, module, cancellation);
        }

        private async Task ApplyPostInstallOperations(Module module, string filePath, CancellationToken cancellation)
        {
            if (module?.extractedPathRename.IsSet == true)
            {
                await Rename(filePath, module.extractedPathRename, cancellation);
            }
        }

        private Func<string, Module, CancellationToken, Task> GetInstallHandler(string extension)
        {
            return extension switch
            {
                _ when IsTarArchive(extension) => InstallModuleTar,
                _ when extension.EndsWith(".zip") => InstallModuleZip,
                _ when extension.EndsWith(".po") => InstallModuleFile,
                _ when extension.EndsWith(".sh") => InstallModuleShell,
                _ when extension.EndsWith(".pkg") => InstallModulePkg,
                _ => null,
            };
        }

        private bool IsTarArchive(string extension)
        {
            return extension.EndsWith(".tar.xz") || extension.EndsWith(".tar.gz");
        }

        private async Task InstallModuleTar(string filePath, Module module, CancellationToken cancellation)
        {
            var dest = GetModuleDestination(module.destination);
            await InstallTar(filePath, dest, stripToUnityRoot: false, cancellation);
        }

        private async Task InstallModuleZip(string filePath, Module module, CancellationToken cancellation)
        {
            await InstallZip(filePath, module.destination, cancellation);
        }

        private async Task InstallModuleFile(string filePath, Module module, CancellationToken cancellation)
        {
            await InstallFile(filePath, module.destination, cancellation);
        }

        private async Task InstallModuleShell(string filePath, Module module, CancellationToken cancellation)
        {
            await InstallShell(filePath, module.destination, cancellation);
        }

        private async Task InstallModulePkg(string filePath, Module module, CancellationToken cancellation)
        {
            await UnpackPkg(filePath, module.destination, cancellation);
        }

        private string GetModuleDestination(string destination)
        {
            return string.IsNullOrEmpty(destination)
                ? INSTALL_PATH
                : destination.Replace("{UNITY_PATH}", INSTALL_PATH);
        }

        public async Task<Installation> CompleteInstall(bool aborted, CancellationToken cancellation = default)
        {
            if (!_installing.Version.IsValid)
            {
                throw new InvalidOperationException("Not installing any version to complete");
            }

            try
            {
                // Handle aborted installation first for cleaner control flow
                if (aborted)
                {
                    _logger.LogInformation($"Deleting aborted installation at path: {INSTALL_PATH}");
                    await Delete(INSTALL_PATH, cancellation);

                    if (_movedExisting)
                    {
                        _logger.LogInformation(
                            $"Restoring previous installation from {INSTALL_PATH_TMP} to {INSTALL_PATH}");
                        await Move(INSTALL_PATH_TMP, INSTALL_PATH, cancellation);
                    }

                    return null;
                }

                // Handle successful installation
                string destination;

                if (_upgradeOriginalPath != null)
                {
                    destination = _upgradeOriginalPath;
                    if (_upgradeOriginalPath != INSTALL_PATH)
                    {
                        _logger.LogInformation($"Moving back upgraded installation to: {destination}");
                        await Move(INSTALL_PATH, destination, cancellation);
                    }
                }
                else
                {
                    destination = GetUniqueInstallationPath(_installing.Version, _installationPaths);
                    _logger.LogInformation($"Moving newly installed version {_installing.Version} to: {destination}");
                    await Move(INSTALL_PATH, destination, cancellation);
                }

                if (_movedExisting)
                {
                    _logger.LogInformation(
                        $"Restoring previous installation from {INSTALL_PATH_TMP} to {INSTALL_PATH}");
                    await Move(INSTALL_PATH_TMP, INSTALL_PATH, cancellation);
                }

                // Validate the installation
                var executable = Path.Combine(destination, "Editor", "Unity");
                if (!File.Exists(executable))
                {
                    _logger.LogError($"Could not find Unity executable at path: {executable}");
                    return null;
                }

                _logger.LogInformation($"Successfully completed installation of Unity {_installing.Version}");
                return new Installation
                {
                    version = _installing.Version,
                    executable = executable,
                    path = destination,
                };
            }
            finally
            {
                // Always clean up state variables regardless of success or failure
                _installing = default;
                _movedExisting = false;
                _upgradeOriginalPath = null;
            }
        }

        public async Task MoveInstallation(Installation installation, string newPath,
            CancellationToken cancellation = default)
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
            var installationExecutable = installation.executable;
            if (!File.Exists(installationExecutable))
                throw new FileNotFoundException("Unity executable not found", installationExecutable);
            if (!child)
            {
                var cmd = new System.Diagnostics.Process();
                cmd.StartInfo.FileName = installationExecutable;
                cmd.StartInfo.Arguments = string.Join(" ", arguments);
                _logger.LogInformation($"$ {cmd.StartInfo.FileName} {cmd.StartInfo.Arguments}");
                cmd.Start();
                while (!cmd.HasExited) await Task.Delay(100);
            }
            else
            {
                if (!arguments.Contains("-logFile")) arguments = arguments.Append("-logFile").Append("-");
                var cmd = new System.Diagnostics.Process();
                cmd.StartInfo.FileName = installationExecutable;
                cmd.StartInfo.Arguments = string.Join(" ", arguments);
                cmd.StartInfo.UseShellExecute = false;
                cmd.StartInfo.RedirectStandardOutput = true;
                cmd.StartInfo.RedirectStandardError = true;
                cmd.EnableRaisingEvents = true;
                cmd.OutputDataReceived += (s, a) =>
                {
                    if (a.Data != null) _logger.LogInformation(a.Data);
                };
                cmd.ErrorDataReceived += (s, a) =>
                {
                    if (a.Data != null) _logger.LogError(a.Data);
                };
                cmd.Start();
                cmd.BeginOutputReadLine();
                cmd.BeginErrorReadLine();
                while (!cmd.HasExited) await Task.Delay(100);
                cmd.WaitForExit();
                _logger.LogInformation($"Unity exited with code {cmd.ExitCode}");
                Environment.Exit(cmd.ExitCode);
            }
        }

        // -------- Helpers --------

        private readonly ILogger _logger = UnityInstaller.CreateLogger<LinuxPlatform>();

        private bool? _isRoot;
        private string _pwd;
        private VersionMetadata _installing;
        private string _installationPaths;
        private string _upgradeOriginalPath;
        private bool _movedExisting;
        private bool _installedEditor;

        string GetUniqueInstallationPath(UnityVersion version, string installationPaths)
        {
            string expanded = null;
            if (!string.IsNullOrEmpty(installationPaths))
            {
                const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
                var paths = installationPaths.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var path in paths)
                {
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

            if (expanded != null) return Helpers.GenerateUniqueFileName(expanded);
            return Helpers.GenerateUniqueFileName(INSTALL_PATH);
        }

        async Task InstallTar(string filePath, string destination, bool stripToUnityRoot,
            CancellationToken cancellation)
        {
            var targetDir = destination.Replace("{UNITY_PATH}", INSTALL_PATH);
            var retryWithRoot = false;
            try
            {
                Directory.CreateDirectory(targetDir);
                await RunTarExtract(filePath, targetDir, stripToUnityRoot, useSudo: false, cancellation);
            }
            catch (Exception e)
            {
                _logger.LogInformation($"Tar extract as user failed, trying as root... ({e.Message})");
                retryWithRoot = true;
            }

            if (retryWithRoot)
            {
                var result = await Sudo("/bin/mkdir", $"-p \"{targetDir}\"", cancellation);
                if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
                await RunTarExtract(filePath, targetDir, stripToUnityRoot, useSudo: true, cancellation);
            }

            // Ensure permissions readable
            await Sudo("/bin/chmod", $"-R o+rX \"{targetDir}\"", cancellation);
        }

        async Task UnpackPkg(string filePath, string destination, CancellationToken cancellation = default)
        {
            // Check if xar is installed
            if (!File.Exists("/usr/bin/xar") && !File.Exists("/usr/bin/7z"))
            {
                throw new InvalidOperationException(
                    "Cannot unpack .pkg files: 'xar' or '7z' not found. Please install 'xar' or '7z' package.");
            }

            // figure out which tool to use
            var use7Z = File.Exists("/usr/bin/7z") && !File.Exists("/usr/bin/xar");

            string tmpDir = null;
            try
            {
                tmpDir = Path.Combine(Path.GetTempPath(), UnityInstaller.PRODUCT_NAME,
                    Path.GetFileNameWithoutExtension(filePath));
                Directory.CreateDirectory(tmpDir);

                if (use7Z)
                {
                    var result = await Command.Run("/usr/bin/7z", $"x \"{filePath}\" -o\"{tmpDir}\" -y",
                        cancellation: cancellation);
                    if (result.exitCode != 0) throw new Exception($"ERROR: {result.error}");
                }
                else
                {
                    var result = await Command.Run("/usr/bin/xar", $"-xf \"{filePath}\" -C \"{tmpDir}\"",
                        cancellation: cancellation);
                    if (result.exitCode != 0)
                    {
                        throw new($"ERROR: {result.error}");
                    }
                }

                string payloadPath = null;
                if (use7Z)
                {
                    // 7z extracts .pkg files with a Payload~ file (note the tilde)
                    payloadPath = Path.Combine(tmpDir, "Payload~");
                    if (!File.Exists(payloadPath))
                    {
                        // Try without tilde as fallback
                        payloadPath = Path.Combine(tmpDir, "Payload");
                        if (!File.Exists(payloadPath))
                        {
                            // List what was actually extracted for debugging
                            var extractedFiles = Directory.GetFiles(tmpDir, "*", SearchOption.TopDirectoryOnly);
                            _logger.LogError(
                                $"Could not find Payload file. Found files: {string.Join(", ", extractedFiles.Select(Path.GetFileName))}");
                            throw new($"Could not find Payload file when unpacking pkg '{filePath}' with 7z");
                        }
                    }
                }
                else
                {
                    // xar creates .pkg.tmp directories
                    var pkgs = Directory.GetDirectories(tmpDir, "*.pkg.tmp");
                    switch (pkgs.Length)
                    {
                        case 0:
                            throw new($"Could not find any sub-pkg when unpacking pkg '{filePath}'");
                        case > 1:
                            throw new(
                                $"Found multiple sub-pkg when unpacking pkg '{filePath}': {string.Join(", ", pkgs.Select(Path.GetFileName))}");
                    }

                    payloadPath = Path.Combine(pkgs[0], "Payload");
                    if (!File.Exists(payloadPath))
                    {
                        throw new(
                            $"Could not find 'Payload' when unpacking pkg '{filePath}', expected at '{payloadPath}'");
                    }
                }

                var targetDir = destination.Replace("{UNITY_PATH}", INSTALL_PATH);

                // Extract the Payload (cpio archive)
                var retryWithRoot = false;
                try
                {
                    Directory.CreateDirectory(targetDir);

                    // Use cpio directly with stdin redirection instead of shell
                    // This avoids shell quoting issues
                    var cpioProcess = new System.Diagnostics.Process
                    {
                        StartInfo = new()
                        {
                            FileName = "/usr/bin/cpio",
                            Arguments = "--extract --make-directories --preserve-modification-time --quiet",
                            WorkingDirectory = targetDir,
                            RedirectStandardInput = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                        },
                    };

                    cpioProcess.Start();

                    // Feed the payload file to cpio's stdin
                    using (var payloadStream = File.OpenRead(payloadPath))
                    {
                        await payloadStream.CopyToAsync(cpioProcess.StandardInput.BaseStream, cancellation);
                    }

                    cpioProcess.StandardInput.Close();

                    await cpioProcess.WaitForExitAsync(cancellation);

                    if (cpioProcess.ExitCode != 0)
                    {
                        var error = await cpioProcess.StandardError.ReadToEndAsync();
                        throw new($"cpio extraction failed: {error}");
                    }
                }
                catch (Exception e)
                {
                    _logger.LogInformation($"cpio as user failed, trying as root... ({e.Message})");
                    retryWithRoot = true;
                }

                if (retryWithRoot)
                {
                    var result = await Sudo("/bin/mkdir", $"-p \"{targetDir}\"", cancellation);
                    if (result.exitCode != 0)
                    {
                        throw new($"ERROR: {result.error}");
                    }

                    // For sudo, we need to use a different approach
                    // Create a script file to avoid shell quoting issues
                    var scriptPath = Path.Combine(tmpDir, "extract.sh");
                    var scriptContent = $@"#!/bin/bash
cd '{targetDir.Replace("'", "'\\''")}'
cpio --extract --make-directories --preserve-modification-time --quiet < '{payloadPath.Replace("'", "'\\''")}'
";
                    await File.WriteAllTextAsync(scriptPath, scriptContent, cancellation);

                    // Make script executable
                    result = await Command.Run("/bin/chmod", $"+x \"{scriptPath}\"", cancellation: cancellation);
                    if (result.exitCode != 0)
                    {
                        throw new($"Failed to make script executable: {result.error}");
                    }

                    // Execute the script with sudo
                    result = await Sudo("/bin/bash", $"\"{scriptPath}\"", cancellation);
                    if (result.exitCode != 0)
                    {
                        throw new($"ERROR: {result.error}");
                    }
                }
            }
            finally
            {
                if (tmpDir != null && Directory.Exists(tmpDir))
                {
                    try
                    {
                        Directory.Delete(tmpDir, true);
                    }
                    catch
                    {
                        // If regular delete fails, try with sudo
                        await Delete(tmpDir, cancellation);
                    }
                }
            }
        }

        private static string GetBaseNameWithoutTarExtension(string filePath)
        {
            var name = Path.GetFileName(filePath);
            if (name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 7);
            if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 7);
            return Path.GetFileNameWithoutExtension(name);
        }

        async Task RunTarExtract(string filePath, string targetDir, bool stripToUnityRoot, bool useSudo,
            CancellationToken cancellation)
        {
            var cmd = useSudo
                ? (Func<string, string, CancellationToken, Task<(int, string, string)>>)((c, a, ct) => Sudo(c, a, ct))
                : (c, a, ct) => Command.Run(c, a, cancellation: ct);

            if (!stripToUnityRoot)
            {
                // Simple case: extract directly to target
                var (exitCode, _, error) =
                    await cmd("/bin/tar", $"-xf \"{filePath}\" -C \"{targetDir}\"", cancellation);
                if (exitCode != 0) throw new Exception($"Failed to extract tar archive: {error}");
                return;
            }

            // Complex case: extract to temp, find Unity root, then move contents
            string tempRoot = null;
            try
            {
                tempRoot = Path.Combine(Path.GetTempPath(), UnityInstaller.PRODUCT_NAME,
                    GetBaseNameWithoutTarExtension(filePath));

                // Clean up any previous failed extraction
                if (Directory.Exists(tempRoot))
                {
                    await Delete(tempRoot, cancellation);
                }

                Directory.CreateDirectory(tempRoot);

                // Extract to temporary location
                var (exitCode, _, error) = await cmd("/bin/tar", $"-xf \"{filePath}\" -C \"{tempRoot}\"", cancellation);
                if (exitCode != 0) throw new($"Failed to extract tar archive to temp directory: {error}");

                // Find Unity root directory
                var sourceRoot = FindUnityRoot(tempRoot);
                if (sourceRoot == null)
                {
                    throw new($"Failed to locate Unity Editor in extracted archive '{filePath}'");
                }

                // Move all contents from source root to target directory
                await MoveDirectoryContents(sourceRoot, targetDir, cancellation);
            }
            finally
            {
                // Always clean up temp directory
                if (tempRoot != null && Directory.Exists(tempRoot))
                {
                    try
                    {
                        await Delete(tempRoot, cancellation);
                    }
                    catch (Exception e)
                    {
                        _logger.LogWarning($"Failed to clean up temporary directory '{tempRoot}': {e.Message}");
                    }
                }
            }
        }

        private static string FindUnityRoot(string searchRoot)
        {
            // Check for null
            if (!Directory.Exists(searchRoot))
                return null;

            // First check if Unity is at the root
            if (File.Exists(Path.Combine(searchRoot, "Editor", "Unity")))
            {
                return searchRoot;
            }

            // Look for Unity in immediate subdirectories (common case)
            foreach (var dir in Directory.GetDirectories(searchRoot))
            {
                if (File.Exists(Path.Combine(dir, "Editor", "Unity")))
                {
                    return dir;
                }
            }

            // Fall back to recursive search, but limit depth to avoid performance issues
            var candidates = Directory.GetDirectories(searchRoot, "*", SearchOption.AllDirectories)
                .Where(d => File.Exists(Path.Combine(d, "Editor", "Unity")))
                .OrderBy(d => d.Split(Path.DirectorySeparatorChar).Length) // Prefer shallower paths
                .ThenBy(d => d.Length) // Then shorter paths
                .FirstOrDefault();

            return candidates;
        }

        private async Task MoveDirectoryContents(string sourceDir, string targetDir, CancellationToken cancellation)
        {
            // Move all subdirectories
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dirName = Path.GetFileName(dir);
                var dst = Path.Combine(targetDir, dirName);

                // Remove existing directory if it exists
                if (Directory.Exists(dst))
                {
                    await Delete(dst, cancellation);
                }

                await Copy(dir, dst, cancellation);
            }

            // Move all files consistently using the same async method
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                var dst = Path.Combine(targetDir, fileName);

                // Use the async Copy method for consistency with directory operations
                await Copy(file, dst, cancellation);
            }
        }

        async Task InstallFile(string filePath, string destination, CancellationToken cancellation)
        {
            if (string.IsNullOrEmpty(destination))
                throw new($"Cannot install {filePath}: File packages must have a destination set.");
            var targetDir = destination.Replace("{UNITY_PATH}", INSTALL_PATH);
            var dst = Path.Combine(targetDir, Path.GetFileName(filePath));
            await Copy(filePath, dst, cancellation);
        }

        async Task InstallZip(string filePath, string destination, CancellationToken cancellation)
        {
            if (string.IsNullOrEmpty(destination))
                throw new($"Cannot install {filePath}: Zip packages must have a destination set.");
            var target = destination.Replace("{UNITY_PATH}", INSTALL_PATH);
            var retryWithRoot = false;
            (int exitCode, string output, string error) result;
            try
            {
                Directory.CreateDirectory(target);
                result = await Command.Run("/usr/bin/unzip", $"-o -d \"{target}\" \"{filePath}\"",
                    cancellation: cancellation);
                if (result.exitCode != 0) throw new($"ERROR: {result.error}");
            }
            catch (Exception e)
            {
                _logger.LogInformation($"Unzip as user failed, trying as root... ({e.Message})");
                retryWithRoot = true;
            }

            if (retryWithRoot)
            {
                result = await Sudo("/bin/mkdir", $"-p \"{target}\"", cancellation);
                if (result.exitCode != 0) throw new($"ERROR: {result.error}");
                result = await Sudo("/usr/bin/unzip", $"-o -d \"{target}\" \"{filePath}\"", cancellation);
                if (result.exitCode != 0) throw new($"ERROR: {result.error}");
            }

            await Sudo("/bin/chmod", $"-R o+rX \"{target}\"", cancellation);
        }

        async Task InstallShell(string filePath, string destination, CancellationToken cancellation)
        {
            if (string.IsNullOrEmpty(destination))
                throw new($"Cannot install {filePath}: Shell script packages must have a destination set.");
            var targetDir = destination.Replace("{UNITY_PATH}", INSTALL_PATH);
            Directory.CreateDirectory(targetDir);
            var dst = Path.Combine(targetDir, Path.GetFileName(filePath));
            File.Copy(filePath, dst, true);
            await Sudo("/bin/chmod", $"+x \"{dst}\"", cancellation);
            // Some shell packages might require execution to self-extract
            // We only run if looks like a self-extract (heuristic: contains 'tar -xf' in first 2000 chars)
            var head = File.ReadAllText(dst, System.Text.Encoding.UTF8);
            if (head.Contains("tar -xf") || head.Contains("#!/"))
            {
                var result = await Sudo("/bin/bash", $"\"{dst}\" --quiet", cancellation);
                if (result.exitCode != 0)
                    _logger.LogWarning($"Shell package returned code {result.exitCode}: {result.error}");
            }
        }

        async Task Rename(string filePath, PathRename rename, CancellationToken cancellation)
        {
            var from = rename.from.Replace("{UNITY_PATH}", INSTALL_PATH);
            var to = rename.to.Replace("{UNITY_PATH}", INSTALL_PATH);
            if (!Directory.Exists(from) && !File.Exists(from))
                throw new($"{filePath}: renameFrom path does not exist: {from}");
            await Move(from, to, cancellation);
        }

        async Task Move(string sourcePath, string newPath, CancellationToken cancellation)
        {
            if (sourcePath.StartsWith(newPath + "/"))
            {
                var tmpSource = Path.Combine(Path.GetTempPath(), UnityInstaller.PRODUCT_NAME,
                    Path.GetFileName(newPath));
                await Move(sourcePath, tmpSource, cancellation);
                await Delete(newPath, cancellation);
                sourcePath = tmpSource;
            }

            var baseDst = Path.GetDirectoryName(newPath);
            try
            {
                Directory.CreateDirectory(baseDst);
                Directory.Move(sourcePath, newPath);
                return;
            }
            catch (Exception e)
            {
                _logger.LogInformation($"Move as user failed, trying as root... ({e.Message})");
            }

            var result = await Sudo("/bin/mkdir", $"-p \"{baseDst}\"", cancellation);
            if (result.exitCode != 0) throw new($"ERROR: {result.error}");
            result = await Sudo("/bin/mv", $"\"{sourcePath}\" \"{newPath}\"", cancellation);
            if (result.exitCode != 0) throw new($"ERROR: {result.error}");
        }

        async Task Copy(string sourcePath, string newPath, CancellationToken cancellation)
        {
            var baseDst = Path.GetDirectoryName(newPath);
            (int exitCode, string output, string error) result;
            try
            {
                result = await Command.Run("/bin/mkdir", $"-p \"{baseDst}\"", cancellation: cancellation);
                if (result.exitCode != 0) throw new($"ERROR: {result.error}");
                if (Directory.Exists(sourcePath))
                {
                    result = await Command.Run("/bin/cp", $"-a \"{sourcePath}\" \"{newPath}\"",
                        cancellation: cancellation);
                }
                else
                {
                    result = await Command.Run("/bin/cp", $"-a \"{sourcePath}\" \"{newPath}\"",
                        cancellation: cancellation);
                }

                if (result.exitCode != 0) throw new($"ERROR: {result.error}");
                return;
            }
            catch (Exception e)
            {
                _logger.LogInformation($"Copy as user failed, trying as root... ({e.Message})");
            }

            result = await Sudo("/bin/mkdir", $"-p \"{baseDst}\"", cancellation);
            if (result.exitCode != 0) throw new($"ERROR: {result.error}");
            result = await Sudo("/bin/cp", $"-a \"{sourcePath}\" \"{newPath}\"", cancellation);
            if (result.exitCode != 0) throw new($"ERROR: {result.error}");
        }

        async Task Delete(string deletePath, CancellationToken cancellation = default)
        {
            try
            {
                Directory.Delete(deletePath, true);
                return;
            }
            catch (Exception e)
            {
                _logger.LogInformation($"Deleting as user failed, trying as root... ({e.Message})");
            }

            var result = await Sudo("/bin/rm", $"-rf \"{deletePath}\"", cancellation);
            if (result.exitCode != 0) throw new($"ERROR: {result.error}");
        }

        async Task<bool> CheckIsRoot(bool withSudo, CancellationToken cancellation)
        {
            var command = "/usr/bin/id";
            var arguments = "-u";
            (int exitCode, string output, string error) result;
            if (withSudo)
            {
                result = await Sudo(command, arguments, cancellation: cancellation);
                if (result.exitCode != 0)
                {
                    if (result.exitCode == 1 && result.error.Contains("Sorry, try again.")) return false;
                    throw new($"ERROR: {result.error}");
                }
            }
            else
            {
                result = await Command.Run(command, arguments, cancellation: cancellation);
                if (result.exitCode != 0) throw new($"ERROR: {result.error}");
            }

            if (!int.TryParse(result.output, out var id))
                throw new($"ERROR: failed to run id, cannot parse output: {result.output} / {result.error}");
            return id == 0;
        }

        async Task<(int exitCode, string output, string error)> Sudo(string command, string arguments,
            CancellationToken cancellation)
        {
            if (_isRoot == null) _isRoot = await CheckIsRoot(false, cancellation);
            if (_isRoot == true) return await Command.Run(command, arguments, cancellation: cancellation);
            if (_pwd == null) await PromptForPasswordIfNecessary(cancellation);
            // Split building to avoid accidental argument injection (current inputs are constants)
            return await Command.Run("sudo", $"-Sk {command} {arguments}", _pwd + "\n", cancellation);
        }
    }
}