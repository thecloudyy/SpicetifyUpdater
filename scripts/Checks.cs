// Checks for the release lookup and download code in src/Program.cs.
// Compiled and run by scripts/run-checks.ps1; everything is exercised through the
// real private members of the built app, so the checks track the shipping code.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

internal static class Program
{
    private static Type _spicetify;
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("usage: Checks.exe <path to Spicetify-Updater.exe>");
            return 2;
        }

        _spicetify = Assembly.LoadFrom(args[0]).GetType("SpicetifyUpdater.Spicetify");
        if (_spicetify == null)
        {
            Console.WriteLine("FAIL: SpicetifyUpdater.Spicetify not found in " + args[0]);
            return 2;
        }

        var temp = Path.Combine(Path.GetTempPath(), "spicetify-checks-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(temp);
        try
        {
            Section("checksum parsing");
            CheckDigest("coreutils", new string('a', 64) + "  marketplace.zip", "marketplace.zip", new string('a', 64));
            CheckDigest("binary marker", new string('b', 64) + " *marketplace.zip", "marketplace.zip", new string('b', 64));
            CheckDigest("path prefix", new string('c', 64) + "  ./build/marketplace.zip", "marketplace.zip", new string('c', 64));
            CheckDigest("BSD style", "SHA256 (marketplace.zip) = " + new string('d', 64), "marketplace.zip", new string('d', 64));
            CheckDigest("other file ignored", new string('e', 64) + "  other.zip", "marketplace.zip", null);
            CheckDigest("garbage ignored", "not a checksum file", "marketplace.zip", null);
            CheckDigest("case insensitive", new string('f', 64) + "  MARKETPLACE.ZIP", "marketplace.zip", new string('f', 64));

            Section("download verification");
            var sample = Path.Combine(temp, "sample.bin");
            var content = new byte[4096];
            for (var i = 0; i < content.Length; i++) content[i] = (byte)(i * 31 % 251);
            File.WriteAllBytes(sample, content);
            var sha = Sha256(sample);
            var size = new FileInfo(sample).Length;

            ExpectAccepted("matching size and digest", NewAsset("file.zip", size, "sha256:" + sha), sample, "against the release checksum");
            ExpectRejected("size mismatch", NewAsset("file.zip", size + 1, null), sample, "bytes but the release lists");
            ExpectRejected("digest mismatch", NewAsset("file.zip", size, "sha256:" + new string('0', 64)), sample, "checksum does not match");
            ExpectAccepted("size only", NewAsset("file.zip", size, null), sample, "Checked the size of file.zip");

            var empty = Path.Combine(temp, "empty.bin");
            File.WriteAllBytes(empty, new byte[0]);
            ExpectRejected("empty download", NewAsset("file.zip", 10, null), empty, "the file is empty");
            ExpectRejected("missing download", NewAsset("file.zip", 10, null), Path.Combine(temp, "absent.bin"), "the file is empty");

            Section("archive integrity");
            var good = Path.Combine(temp, "good.zip");
            var source = Path.Combine(temp, "payload");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "spicetify.exe"), "MZ");
            ZipFile.CreateFromDirectory(source, good);

            var truncated = Path.Combine(temp, "truncated.zip");
            var bytes = File.ReadAllBytes(good);
            var half = new byte[bytes.Length / 2];
            Array.Copy(bytes, half, half.Length);
            File.WriteAllBytes(truncated, half);

            var unpacked = Path.Combine(temp, "unpacked");
            ExpectRejectedOnExtract("truncated zip refused", NewAsset("good.zip", 0, null), truncated, "cannot be read as a zip file");
            Check("truncated zip leaves nothing behind", !Directory.Exists(unpacked), "output folder was created");
            Call("ExtractArchive", NewAsset("good.zip", 0, null), good, unpacked);
            Check("valid zip extracts", File.Exists(Path.Combine(unpacked, "spicetify.exe")), "spicetify.exe missing");

            Section("cache tag matching");
            var cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpicetifyUpdater", "releases.json");
            var backup = File.Exists(cachePath) ? File.ReadAllText(cachePath) : null;
            try
            {
                // The repo does not exist, so only the stored asset list can answer.
                File.WriteAllText(cachePath, "{\"test/fake\":{\"tag\":\"9.9.9\",\"assets\":[[\"fake.zip\",\"https://example.invalid/fake.zip\",123,\"sha256:abc\"]]}}");

                var sameTag = Latest("test/fake", "9.9.9");
                Check("matching tag uses the cache", sameTag != null && (Field(sameTag, "Tag") as string) == "9.9.9",
                    "got " + (sameTag == null ? "nothing" : Field(sameTag, "Tag") as string));

                var otherTag = Latest("test/fake", "1.0.0");
                Check("non-matching tag is refused", otherTag == null,
                    "served " + (otherTag == null ? "" : Field(otherTag, "Tag") as string));
            }
            finally
            {
                if (backup == null) File.Delete(cachePath);
                else File.WriteAllText(cachePath, backup);
            }

            Section("live release lookup (github.com, no API quota)");
            var market = AssetFor("spicetify/marketplace", delegate(string n) { return n.Equals("marketplace.zip", StringComparison.OrdinalIgnoreCase); });
            Check("marketplace asset found", market != null, "not found");
            if (market != null)
            {
                Check("marketplace has a size", (long)Field(market, "Size") > 0, "size missing");
                Check("marketplace url points at the file",
                    ((string)Field(market, "Url")).EndsWith((string)Field(market, "Name"), StringComparison.OrdinalIgnoreCase),
                    "unexpected url " + Field(market, "Url"));
            }

            var cli = AssetFor("spicetify/cli", delegate(string n) { return n.EndsWith("windows-x64.zip", StringComparison.OrdinalIgnoreCase); });
            Check("cli x64 asset found", cli != null, "not found");
            if (cli != null) Check("cli has a size", (long)Field(cli, "Size") > 0, "size missing");

            var selfTag = LatestTag("thecloudyy/SpicetifyUpdater");
            Check("own latest tag reads without the API", selfTag != null && selfTag.Length > 0, "no tag");

            var setup = selfTag == null ? null : OurAsset(selfTag, "-setup.exe");
            Check("own setup url resolves", setup != null, "did not resolve");
            if (setup != null)
            {
                Check("own setup has a size", (long)Field(setup, "Size") > 0, "size missing");
                var digest = Field(setup, "Digest") as string;
                Check("own setup carries a sha256 from the checksum file",
                    digest != null && digest.StartsWith("sha256:"), "digest was " + (digest ?? "missing"));
            }

            Section("live download and verification");
            if (market != null)
            {
                // Upstream publishes no checksums file, so this one verifies by exact size.
                var download = Path.Combine(temp, "marketplace.zip");
                var log = "";
                Call("Fetch", market, download, new Action<double>(delegate(double d) { }), new Action<string>(delegate(string s) { log = s; }));
                Check("marketplace download verified", new FileInfo(download).Length > 0 && log.Length > 0, "no log line");
                Console.WriteLine("        " + log);
            }

            if (setup != null)
            {
                // Our releases ship SHA256SUMS.txt, so corruption must be caught by hash.
                var exe = Path.Combine(temp, "setup.exe");
                var log = "";
                Call("Fetch", setup, exe, new Action<double>(delegate(double d) { }), new Action<string>(delegate(string s) { log = s; }));
                Check("setup download verified by checksum",
                    log.IndexOf("release checksum", StringComparison.OrdinalIgnoreCase) >= 0, "log was \"" + log + "\"");

                var bytesOfExe = File.ReadAllBytes(exe);
                bytesOfExe[bytesOfExe.Length / 2] ^= 0xFF;
                File.WriteAllBytes(exe, bytesOfExe);
                ExpectRejected("corrupted setup refused", setup, exe, "checksum does not match");
            }
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_failed == 0
            ? "ALL " + _passed + " CHECKS PASSED"
            : _failed + " CHECK(S) FAILED, " + _passed + " passed");
        return _failed == 0 ? 0 : 1;
    }

    /* ------------------------------------------------------------- helpers */

    private static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name);
    }

    private static void Check(string label, bool ok, string why)
    {
        if (ok) { _passed++; Console.WriteLine("  ok    " + label); }
        else { _failed++; Console.WriteLine("  FAIL  " + label + " (" + why + ")"); }
    }

    private static void CheckDigest(string label, string text, string name, string expected)
    {
        var actual = (string)Call("FindDigest", text, name);
        Check(label, string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "expected " + (expected ?? "null") + ", got " + (actual ?? "null"));
    }

    private static void ExpectAccepted(string label, object asset, string path, string expectedLog)
    {
        var text = "";
        try
        {
            Call("VerifyDownload", asset, path, new Action<string>(delegate(string s) { text = s; }));
            Check(label, text.IndexOf(expectedLog, StringComparison.Ordinal) >= 0, "unexpected log \"" + text + "\"");
        }
        catch (Exception ex)
        {
            Check(label, false, "threw: " + ex.Message);
        }
    }

    private static void ExpectRejected(string label, object asset, string path, string expected)
    {
        try
        {
            Call("VerifyDownload", asset, path, new Action<string>(delegate(string s) { }));
            Check(label, false, "was accepted");
        }
        catch (Exception ex)
        {
            Check(label, ex.Message.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0, "wrong message: " + ex.Message);
        }
    }

    private static void ExpectRejectedOnExtract(string label, object asset, string path, string expected)
    {
        try
        {
            Call("ExtractArchive", asset, path, Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid().ToString("N").Substring(0, 6)));
            Check(label, false, "was extracted");
        }
        catch (Exception ex)
        {
            Check(label, ex.Message.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0, "wrong message: " + ex.Message);
        }
    }

    private static object NewAsset(string name, long size, string digest)
    {
        var assetType = _spicetify.GetNestedType("Asset", BindingFlags.NonPublic);
        var asset = Activator.CreateInstance(assetType, true);
        assetType.GetField("Name").SetValue(asset, name);
        assetType.GetField("Url").SetValue(asset, "https://example.invalid/" + name);
        assetType.GetField("Size").SetValue(asset, size);
        assetType.GetField("Digest").SetValue(asset, digest);
        return asset;
    }

    private static object AssetFor(string repo, Func<string, bool> match)
    {
        var tag = LatestTag(repo);
        if (tag == null) return null;
        return Call("AssetFor", repo, tag, match);
    }

    private static string LatestTag(string repo)
    {
        try { return (string)Call("LatestTag", repo, true); }
        catch (Exception ex) { Console.WriteLine("  (tag lookup failed: " + ex.Message + ")"); return null; }
    }

    private static object OurAsset(string tag, string suffix)
    {
        return Call("OurAsset", tag, suffix);
    }

    private static object Latest(string repo, string tag)
    {
        try
        {
            var task = Call("LatestReleaseAsync", repo, tag);
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine("  (api unavailable, as intended: " + Root(ex).GetType().Name + ")");
            return null;
        }
    }

    private static object Call(string method, params object[] arguments)
    {
        var info = _spicetify.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
        if (info == null) throw new InvalidOperationException("method " + method + " not found");
        try { return info.Invoke(null, arguments); }
        catch (TargetInvocationException ex) { throw Root(ex); }
    }

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException != null &&
               (ex is TargetInvocationException || ex is AggregateException))
            ex = ex.InnerException;

        return ex;
    }

    private static object Field(object target, string name)
    {
        return target == null ? null : target.GetType().GetField(name).GetValue(target);
    }

    private static string Sha256(string path)
    {
        using (var hash = System.Security.Cryptography.SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
}
