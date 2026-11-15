using System.Runtime.InteropServices;
using Kite.Core.Storage;
using Kite.Management.Core;
using Xunit;

namespace Kite.UnitTests;

public sealed class CrossPlatformPackagingUnitTests
{
    [Fact]
    public void KiteHomeLayout_ResolvesPlatformDefaults_ForWindowsMacOsAndLinux()
    {
        string winUser = KiteHomeLayout.ResolveDefaultDataDirectory(
            OSPlatform.Windows,
            isPrivilegedService: false,
            localAppData: @"C:\Users\alice\AppData\Local",
            appData: @"C:\Users\alice\AppData\Roaming",
            commonAppData: @"C:\ProgramData",
            userProfile: @"C:\Users\alice",
            xdgDataHome: null,
            _ => false);
        Assert.Equal(Path.Combine(@"C:\Users\alice\AppData\Local", "Kite", "kite-data"), winUser);

        string winService = KiteHomeLayout.ResolveDefaultDataDirectory(
            OSPlatform.Windows,
            isPrivilegedService: true,
            localAppData: @"C:\WINDOWS\system32\config\systemprofile\AppData\Local",
            appData: null,
            commonAppData: @"C:\ProgramData",
            userProfile: null,
            xdgDataHome: null,
            _ => false);
        Assert.Equal(Path.Combine(@"C:\ProgramData", "Kite", "kite-data"), winService);

        string macUser = KiteHomeLayout.ResolveDefaultDataDirectory(
            OSPlatform.OSX,
            isPrivilegedService: false,
            localAppData: "/Users/alice/.local/share",
            appData: "/Users/alice/Library/Application Support",
            commonAppData: "/Library/Application Support",
            userProfile: "/Users/alice",
            xdgDataHome: null,
            _ => false);
        Assert.Equal(Path.Combine("/Users/alice/Library/Application Support", "Kite", "kite-data"), macUser);

        string macLegacy = KiteHomeLayout.ResolveDefaultDataDirectory(
            OSPlatform.OSX,
            isPrivilegedService: false,
            localAppData: "/Users/alice/.local/share",
            appData: "/Users/alice/Library/Application Support",
            commonAppData: null,
            userProfile: "/Users/alice",
            xdgDataHome: null,
            p => p.Contains(".local", StringComparison.Ordinal));
        Assert.Equal(Path.Combine("/Users/alice/.local/share", "Kite", "kite-data"), macLegacy);

        string macRoot = KiteHomeLayout.ResolveDefaultDataDirectory(
            OSPlatform.OSX,
            isPrivilegedService: true,
            localAppData: "/var/root/.local/share",
            appData: null,
            commonAppData: null,
            userProfile: "/var/root",
            xdgDataHome: null,
            _ => false);
        Assert.Equal("/var/lib/kite", macRoot);

        string linuxXdg = KiteHomeLayout.ResolveDefaultDataDirectory(
            OSPlatform.Linux,
            isPrivilegedService: false,
            localAppData: "/home/alice/.local/share",
            appData: null,
            commonAppData: null,
            userProfile: "/home/alice",
            xdgDataHome: "/home/alice/.xdg-data",
            _ => false);
        Assert.Equal(Path.Combine("/home/alice/.xdg-data", "kite", "kite-data"), linuxXdg);

        string linuxRoot = KiteHomeLayout.ResolveDefaultDataDirectory(
            OSPlatform.Linux,
            isPrivilegedService: true,
            localAppData: "/root/.local/share",
            appData: null,
            commonAppData: null,
            userProfile: "/root",
            xdgDataHome: null,
            _ => false);
        Assert.Equal("/var/lib/kite", linuxRoot);
    }

    [Fact]
    public void KiteInstanceRegistry_ResolvesPlatformDefaults_ForWindowsMacOsAndLinux()
    {
        string winUser = KiteInstanceRegistry.ResolveDefaultRegistryDirectory(
            OSPlatform.Windows,
            isPrivilegedService: false,
            localAppData: @"C:\Users\alice\AppData\Local",
            appData: null,
            commonAppData: @"C:\ProgramData",
            userProfile: @"C:\Users\alice",
            xdgRuntimeDir: null,
            xdgDataHome: null,
            _ => false);
        Assert.Equal(Path.Combine(@"C:\Users\alice\AppData\Local", "Kite", "instances"), winUser);

        string linuxRuntime = KiteInstanceRegistry.ResolveDefaultRegistryDirectory(
            OSPlatform.Linux,
            isPrivilegedService: false,
            localAppData: "/home/alice/.local/share",
            appData: null,
            commonAppData: null,
            userProfile: "/home/alice",
            xdgRuntimeDir: "/run/user/1000",
            xdgDataHome: null,
            _ => false);
        Assert.Equal(Path.Combine("/run/user/1000", "kite", "instances"), linuxRuntime);

        string linuxRoot = KiteInstanceRegistry.ResolveDefaultRegistryDirectory(
            OSPlatform.Linux,
            isPrivilegedService: true,
            localAppData: "/root/.local/share",
            appData: null,
            commonAppData: null,
            userProfile: "/root",
            xdgRuntimeDir: null,
            xdgDataHome: null,
            _ => false);
        Assert.Equal("/run/kite/instances", linuxRoot);

        string macUser = KiteInstanceRegistry.ResolveDefaultRegistryDirectory(
            OSPlatform.OSX,
            isPrivilegedService: false,
            localAppData: "/Users/alice/.local/share",
            appData: "/Users/alice/Library/Application Support",
            commonAppData: null,
            userProfile: "/Users/alice",
            xdgRuntimeDir: null,
            xdgDataHome: null,
            _ => false);
        Assert.Equal(Path.Combine("/Users/alice/Library/Application Support", "Kite", "instances"), macUser);
    }

    [Fact]
    public void LocateKiteServerLaunchTarget_PrefersCoLocatedNativeBinary_ThenDll_ThenCliSelfFallback()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kite_locate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string nativeExeName = OperatingSystem.IsWindows() ? "Kite.Server.exe" : "Kite.Server";
            string cliExeName = OperatingSystem.IsWindows() ? "kite.exe" : "kite";
            string fakeCli = Path.Combine(tempDir, cliExeName);
            File.WriteAllText(fakeCli, string.Empty);

            // 1. Standalone CLI fallback (`kite run`) when no Kite.Server is alongside
            KiteManagementService.KiteServerLaunchTarget? selfFallback =
                KiteManagementService.LocateKiteServerLaunchTarget(
                    baseDirectoryOverride: tempDir,
                    processPathOverride: fakeCli,
                    envServerPathOverride: string.Empty);
            Assert.NotNull(selfFallback);
            Assert.True(selfFallback!.IsNativeExecutable);
            Assert.Equal(fakeCli, selfFallback.FileName);
            Assert.Equal("run ", selfFallback.ArgumentsPrefix);

            // 2. Managed DLL + runtimeconfig.json fallback via dotnet
            string dllPath = Path.Combine(tempDir, "Kite.Server.dll");
            string rcPath = Path.Combine(tempDir, "Kite.Server.runtimeconfig.json");
            File.WriteAllText(dllPath, string.Empty);
            File.WriteAllText(rcPath, "{}");

            KiteManagementService.KiteServerLaunchTarget? dllTarget =
                KiteManagementService.LocateKiteServerLaunchTarget(
                    baseDirectoryOverride: tempDir,
                    processPathOverride: fakeCli,
                    envServerPathOverride: string.Empty);
            Assert.NotNull(dllTarget);
            Assert.False(dllTarget!.IsNativeExecutable);
            Assert.Equal("dotnet", dllTarget.FileName);
            Assert.Equal(dllPath, dllTarget.ResolvedBinaryPath);

            // 3. Co-located native executable wins over managed DLL
            string nativePath = Path.Combine(tempDir, nativeExeName);
            File.WriteAllText(nativePath, string.Empty);

            KiteManagementService.KiteServerLaunchTarget? nativeTarget =
                KiteManagementService.LocateKiteServerLaunchTarget(
                    baseDirectoryOverride: tempDir,
                    processPathOverride: fakeCli,
                    envServerPathOverride: string.Empty);
            Assert.NotNull(nativeTarget);
            Assert.True(nativeTarget!.IsNativeExecutable);
            Assert.Equal(nativePath, nativeTarget.FileName);
            Assert.Equal(string.Empty, nativeTarget.ArgumentsPrefix);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
