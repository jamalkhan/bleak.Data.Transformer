using System.Diagnostics;
using System.IO.Compression;
using bleak.Data.Transformer.Config;
using bleak.Data.Transformer.Transforms;

namespace bleak.Data.Transformer.Sources;

/// <summary>
/// Transparently materializes a plain CSV for a source file that arrives PGP-encrypted and/or gzipped —
/// detected purely from the configured file name's trailing ".pgp"/".gz" (e.g. "foo.csv.gz.pgp" or
/// "foo.csv.gz"). A file with neither suffix passes through untouched, so this is a no-op for every
/// existing source. Decryption shells out to the system "gpg" binary in batch mode.
///
/// The plain result is written next to the source, into a sibling subfolder named "&lt;file name&gt;_decrypted"
/// (e.g. "foo.csv.gz.pgp_decrypted/foo.csv") — durable on purpose, so the user can browse it afterward,
/// not a throwaway temp file. It's only rebuilt when missing or older than the source (by mtime), and an
/// in-process cache skips even that check on repeat opens — a source is typically opened many times within
/// one run (once per output, relation, and lookup that references it), and decrypting is by far the most
/// expensive part.
///
/// The private key normally just needs to already be in the local gpg keyring. When it isn't — e.g. a
/// fresh machine — a source can point <see cref="DecryptConfig.PgpKeyFile"/> at the key file and it's
/// imported (idempotently) before decrypting; <see cref="DecryptConfig.PgpPassphrase"/> covers a
/// passphrase-protected key, fed to gpg via stdin, never via argv or a log line.
/// </summary>
internal static class EncryptedSourceCache
{
    private static readonly Dictionary<string, string> PlainPathCache = new();
    private static readonly HashSet<string> ImportedKeyFiles = new();
    private static readonly object Lock = new();

    /// <summary>Given a resolved source path, returns a plain-CSV path ready to read — decrypting/decompressing
    /// first if needed. Returns <paramref name="path"/> unchanged when it's already plain.</summary>
    public static string ResolvePlainPath(string path, DecryptConfig? decrypt, string baseDirectory)
    {
        var isPgp = path.EndsWith(".pgp", StringComparison.OrdinalIgnoreCase);
        var withoutPgp = isPgp ? path[..^".pgp".Length] : path;
        var isGz = withoutPgp.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        if (!isPgp && !isGz)
        {
            return path; // already plain — the common case
        }

        var plainName = isGz ? withoutPgp[..^".gz".Length] : withoutPgp;
        var sourceMtime = File.GetLastWriteTimeUtc(path);
        var cacheKey = string.Join("\u0001", path, sourceMtime.Ticks);

        lock (Lock)
        {
            if (PlainPathCache.TryGetValue(cacheKey, out var cached) && File.Exists(cached))
            {
                return cached;
            }

            // Durable, sibling to the source — "<file name>_decrypted/<plain name>" — not a temp/hidden
            // location, so the user can find and browse it afterward.
            var destDir = path + "_decrypted";
            Directory.CreateDirectory(destDir);
            var destPath = Path.Combine(destDir, Path.GetFileName(plainName));

            if (File.Exists(destPath) && File.GetLastWriteTimeUtc(destPath) >= sourceMtime)
            {
                PlainPathCache[cacheKey] = destPath;
                return destPath;
            }

            if (isPgp && decrypt?.PgpKeyFile is { Length: > 0 } keyFile)
            {
                ImportKeyIfNeeded(PathUtil.Resolve(keyFile, baseDirectory));
            }

            Materialize(path, isPgp, isGz, destPath, decrypt);
            PlainPathCache[cacheKey] = destPath;
            return destPath;
        }
    }

    private static void ImportKeyIfNeeded(string keyFilePath)
    {
        if (!ImportedKeyFiles.Add(keyFilePath))
        {
            return; // already imported this process
        }
        if (!File.Exists(keyFilePath))
        {
            throw new EtlConfigException($"pgpKeyFile not found: {keyFilePath}");
        }

        var psi = new ProcessStartInfo("gpg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--batch");
        psi.ArgumentList.Add("--yes");
        psi.ArgumentList.Add("--import");
        psi.ArgumentList.Add(keyFilePath);

        using var process = StartGpg(psi, keyFilePath);
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var stderr = stderrTask.GetAwaiter().GetResult().Trim();
            throw new EtlConfigException(
                $"gpg failed to import key '{Path.GetFileName(keyFilePath)}' (exit {process.ExitCode}): {stderr}");
        }
    }

    private static void Materialize(string sourcePath, bool isPgp, bool isGz, string destPath, DecryptConfig? decrypt)
    {
        var tempOut = $"{destPath}.tmp{Guid.NewGuid():N}";
        try
        {
            if (isPgp && isGz)
            {
                var decryptedGz = $"{destPath}.gz.tmp{Guid.NewGuid():N}";
                try
                {
                    DecryptPgp(sourcePath, decryptedGz, decrypt);
                    Gunzip(decryptedGz, tempOut);
                }
                finally
                {
                    File.Delete(decryptedGz);
                }
            }
            else if (isPgp)
            {
                DecryptPgp(sourcePath, tempOut, decrypt);
            }
            else // gz only
            {
                Gunzip(sourcePath, tempOut);
            }

            File.Move(tempOut, destPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempOut))
            {
                File.Delete(tempOut);
            }
        }
    }

    private static void DecryptPgp(string sourcePath, string destPath, DecryptConfig? decrypt)
    {
        var hasPassphrase = decrypt?.PgpPassphrase is { Length: > 0 };
        var psi = new ProcessStartInfo("gpg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = hasPassphrase,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--batch");
        psi.ArgumentList.Add("--yes");
        if (hasPassphrase)
        {
            // Passphrase is fed over stdin, never as a command-line argument (visible via `ps`) or logged.
            psi.ArgumentList.Add("--pinentry-mode");
            psi.ArgumentList.Add("loopback");
            psi.ArgumentList.Add("--passphrase-fd");
            psi.ArgumentList.Add("0");
        }
        psi.ArgumentList.Add("--decrypt");
        psi.ArgumentList.Add(sourcePath);

        using var process = StartGpg(psi, sourcePath);
        if (hasPassphrase)
        {
            process.StandardInput.Write(decrypt!.PgpPassphrase);
            process.StandardInput.Close();
        }

        var stderrTask = process.StandardError.ReadToEndAsync();
        using (var outFile = File.Create(destPath))
        {
            process.StandardOutput.BaseStream.CopyTo(outFile);
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var stderr = stderrTask.GetAwaiter().GetResult().Trim();
            throw new EtlConfigException(
                $"gpg failed to decrypt '{Path.GetFileName(sourcePath)}' (exit {process.ExitCode}): {stderr}");
        }
    }

    private static Process StartGpg(ProcessStartInfo psi, string forFile)
    {
        try
        {
            return Process.Start(psi) ?? throw new InvalidOperationException("gpg did not start.");
        }
        catch (Exception ex)
        {
            throw new EtlConfigException(
                $"could not run 'gpg' for '{Path.GetFileName(forFile)}': {ex.Message}. Is GnuPG installed and on PATH?");
        }
    }

    private static void Gunzip(string sourcePath, string destPath)
    {
        using var input = File.OpenRead(sourcePath);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = File.Create(destPath);
        gzip.CopyTo(output);
    }
}
