using Primicord;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

// Read-only update discovery. Never downloads/applies a package to the real install.
if (args.Length != 1) throw new ArgumentException("Pass the release directory, or --github after publishing.");
IUpdateSource source = args[0] == "--github"
    ? new GithubSource("https://github.com/" + Updater.DefaultRepo, null, false)
    : new SimpleFileSource(new DirectoryInfo(Path.GetFullPath(args[0])));
string scratch = Path.Combine(Path.GetTempPath(), "primicord-update-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
const string targetVersion = "1.0.0";
if (Updater.CurrentVersion != targetVersion) throw new Exception("App and release versions disagree.");
if (Updater.DisplayPackageVersion("0.9.10") != "0.9.9.1") throw new Exception("Legacy display mapping regressed.");
foreach (string installed in new[] { "0.7.1", "0.8.5", "0.9.1", "0.9.9", "0.9.10", targetVersion })
{
    var locator = new TestVelopackLocator("Primicord", installed, scratch);
    var manager = new UpdateManager(source, new UpdateOptions { ExplicitChannel = "win" }, locator);
    var update = await manager.CheckForUpdatesAsync();
    if (installed == targetVersion)
    {
        if (update != null) throw new Exception("Current package should not receive the same update again.");
    }
    else
    {
        if (update?.TargetFullRelease.Version.ToString() != targetVersion)
            throw new Exception("Wrong update target for " + installed + ": " + update?.TargetFullRelease.Version);
        if (Updater.DisplayPackageVersion(update.TargetFullRelease.Version.ToString()) != targetVersion)
            throw new Exception("Public version mapping is incorrect.");
        if (update.TargetFullRelease.Size <= 0) throw new Exception("Release asset has no size.");
    }
    Console.WriteLine("PASS: " + installed + " → " + (update == null ? "no update" : targetVersion));
}
