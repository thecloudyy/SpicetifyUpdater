// Drives the self-update path of an installed Spicetify Updater build through its own private
// members, so what runs here is the code the app actually ships, not a reimplementation.
//
//   SelfUpdateProbe.exe "<path to installed Spicetify-Updater.exe>"            (an update exists)
//   SelfUpdateProbe.exe "<path to installed Spicetify-Updater.exe>" --current  (up to date)
//
// In the default mode the app is expected to offer an update; with --current it is expected to
// report its own version as the latest and offer nothing. No version numbers are hardcoded.

using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

internal static class Program
{
    private static Type _type;
    private static int _failed;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: SelfUpdateProbe.exe <path to Spicetify-Updater.exe> [--current]");
            return 2;
        }

        var app = args[0];
        var expectCurrent = args.Length > 1 && args[1].Equals("--current", StringComparison.OrdinalIgnoreCase);

        var assembly = Assembly.LoadFrom(app);
        _type = assembly.GetType("SpicetifyUpdater.Spicetify");
        if (_type == null)
        {
            Console.WriteLine("FAIL: SpicetifyUpdater.Spicetify not found in " + app);
            return 2;
        }

        var installed = assembly.GetName().Version.ToString();
        if (installed.EndsWith(".0", StringComparison.Ordinal)) installed = installed.Substring(0, installed.Length - 2);

        var temp = Path.Combine(Path.GetTempPath(), "selfupdate-probe-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(temp);
        try
        {
            Console.WriteLine("installed exe:  " + app);
            Console.WriteLine("exe version:    " + installed);
            Console.WriteLine("self version:   " + Call("SelfVersion"));
            Console.WriteLine("mode:           " + (expectCurrent ? "expect up to date" : "expect an update"));

            Console.WriteLine();
            Console.WriteLine("== status the app shows (this is what decides 'update available')");
            var status = Call("GetStatus", true);
            var selfVersion = (string)Field(status, "SelfVersion");
            var selfLatest = (string)Field(status, "SelfLatest");
            var selfUpdate = (bool)Field(status, "SelfUpdate");
            var error = (string)Field(status, "ReleaseError");
            var fromCache = (bool)Field(status, "VersionsFromCache");
            Console.WriteLine("  SelfVersion:  " + selfVersion);
            Console.WriteLine("  SelfLatest:   " + selfLatest);
            Console.WriteLine("  SelfUpdate:   " + selfUpdate);
            Console.WriteLine("  from cache:   " + fromCache);
            Console.WriteLine("  error:        " + (error ?? "none"));

            Check("SelfVersion is the installed version",
                string.Equals(selfVersion, installed, StringComparison.OrdinalIgnoreCase),
                "status said " + (selfVersion ?? "(null)") + ", exe is " + installed);
            Check("the status lookup did not fail", error == null, "error was " + error);
            Check("the status is not served from cache", !fromCache, "versions came from cache");

            if (expectCurrent)
            {
                Check("SelfLatest is the installed version",
                    string.Equals(selfLatest, installed, StringComparison.OrdinalIgnoreCase),
                    "SelfLatest was " + (selfLatest ?? "(null)"));
                Check("SelfUpdate is false", !selfUpdate, "the app still offers an update");
            }
            else
            {
                Check("SelfLatest is something else", !string.Equals(selfLatest, installed, StringComparison.OrdinalIgnoreCase),
                    "SelfLatest was " + (selfLatest ?? "(null)"));
                Check("SelfUpdate is true", selfUpdate, "the app offers no update");
            }

            Console.WriteLine();
            Console.WriteLine("== latest tag, read from github.com rather than the API");
            var tag = (string)Call("LatestTag", "thecloudyy/SpicetifyUpdater", true);
            Console.WriteLine("  tag:          " + (tag ?? "(null)") + "  (internal form, no leading v)");
            Check("the tag lookup answered", !string.IsNullOrEmpty(tag), "no tag");
            Check("the tag agrees with the status", string.Equals(tag, selfLatest, StringComparison.OrdinalIgnoreCase),
                "tag " + (tag ?? "(null)") + ", status " + (selfLatest ?? "(null)"));

            Console.WriteLine();
            Console.WriteLine("== setup asset, with the checksum taken from the release");
            var setup = Call("OurAsset", tag, "-setup.exe");
            var name = (string)Field(setup, "Name");
            var url = (string)Field(setup, "Url");
            var size = (long)Field(setup, "Size");
            var digest = (string)Field(setup, "Digest");
            Console.WriteLine("  name:         " + name);
            Console.WriteLine("  url:          " + url);
            Console.WriteLine("  size:         " + size);
            Console.WriteLine("  digest:       " + (digest ?? "(none)"));
            Check("the setup asset is chosen",
                name != null && name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase), "got " + (name ?? "(null)"));
            Check("its url points into the release",
                url != null && url.IndexOf("/releases/download/", StringComparison.Ordinal) > 0, "url was " + (url ?? "(null)"));
            Check("it carries a sha256 from SHA256SUMS.txt", digest != null && digest.StartsWith("sha256:"),
                "digest was " + (digest ?? "(missing)"));

            Console.WriteLine();
            Console.WriteLine("== download and verification");
            var target = Path.Combine(temp, name);
            var log = "";
            Call("Fetch", setup, target, new Action<double>(delegate(double ratio) { }),
                new Action<string>(delegate(string message) { log = message; }));

            var actualSize = new FileInfo(target).Length;
            var actualHash = Sha256(target);
            Console.WriteLine("  log:          " + log);
            Console.WriteLine("  downloaded:   " + actualSize + " bytes");
            Console.WriteLine("  sha256:       " + actualHash);
            Check("the download matched the published size", actualSize == size, "got " + actualSize + " bytes");
            Check("the download matched the published sha256",
                digest != null && string.Equals(digest.Substring("sha256:".Length), actualHash, StringComparison.OrdinalIgnoreCase),
                "hash was " + actualHash);
            Check("the app reported verifying against the release checksum",
                log.IndexOf("release checksum", StringComparison.OrdinalIgnoreCase) >= 0, "log was \"" + log + "\"");

            var corrupt = Path.Combine(temp, "corrupt.exe");
            var bytes = File.ReadAllBytes(target);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(corrupt, bytes);
            try
            {
                Call("VerifyDownload", setup, corrupt, new Action<string>(delegate(string message) { }));
                Check("a corrupted setup is refused", false, "was accepted");
            }
            catch (Exception ex)
            {
                Console.WriteLine("  corrupted:    " + ex.Message);
                Check("a corrupted setup is refused",
                    ex.Message.IndexOf("checksum does not match", StringComparison.OrdinalIgnoreCase) >= 0,
                    "wrong message: " + ex.Message);
            }
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "SELF-UPDATE PATH OK" : _failed + " CHECK(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string label, bool ok, string why)
    {
        if (ok) Console.WriteLine("  ok            " + label);
        else { _failed++; Console.WriteLine("  FAIL          " + label + " (" + why + ")"); }
    }

    private static object Call(string name, params object[] arguments)
    {
        foreach (var method in _type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != name || method.GetParameters().Length != arguments.Length) continue;

            try { return method.Invoke(null, arguments); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        throw new InvalidOperationException(name + "(" + arguments.Length + " args) not found");
    }

    private static object Field(object target, string name)
    {
        var field = target == null
            ? null
            : target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return field == null ? null : field.GetValue(target);
    }

    private static string Sha256(string path)
    {
        using (var hash = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
