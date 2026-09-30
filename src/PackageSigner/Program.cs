using System.IO.Abstractions;
using System.Text;
using FileUpdaterPackages;

//Run on the admin's own machine. The private key never goes on the server or in the repository.
//  keygen [--out <dir>]                       makes package-signing.key (private) and package-signing.pub (public)
//  sign   --packages <dir> --key <file>       writes manifest.json and manifest.sig into the packages folder
//  verify --packages <dir> --pub <key|file>   checks a folder's manifest.sig, the way the launcher will
//The key's password comes from PACKAGE_SIGNER_PASSWORD, or is asked for.

const string PasswordVariable = "PACKAGE_SIGNER_PASSWORD";
const string KeyFileName = "package-signing.key";
const string PublicKeyFileName = "package-signing.pub";

try
{
    var options = ParseOptions(args.Skip(1).ToArray());
    return args.FirstOrDefault() switch
    {
        "keygen" => KeyGen(options),
        "sign" => Sign(options),
        "verify" => Verify(options),
        _ => Usage(),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("""
        Usage:
          PackageSigner keygen [--out <dir>]
          PackageSigner sign   --packages <dir> --key <file>
          PackageSigner verify --packages <dir> --pub <base64 key or file>
        """);
    return 2;
}

static int KeyGen(Dictionary<string, string> options)
{
    var outDir = options.GetValueOrDefault("out", ".");
    Directory.CreateDirectory(outDir);
    var keyPath = Path.Combine(outDir, KeyFileName);
    var pubPath = Path.Combine(outDir, PublicKeyFileName);
    if (File.Exists(keyPath)) throw new ArgumentException($"{keyPath} already exists, refusing to overwrite it.");

    var password = Environment.GetEnvironmentVariable(PasswordVariable) ?? ReadNewPassword();
    if (password.Length == 0)
        Console.Error.WriteLine("Warning: the private key is being saved without a password.");

    using var key = ManifestSigning.GenerateKey();
    File.WriteAllText(keyPath, ManifestSigning.ExportPrivateKey(key, password));
    var publicKey = ManifestSigning.ExportPublicKey(key);
    File.WriteAllText(pubPath, publicKey);

    Console.WriteLine($"Private key: {keyPath}  (keep it off the server and out of git)");
    Console.WriteLine($"Public key:  {pubPath}");
    Console.WriteLine();
    Console.WriteLine("Put this public key in the launcher's config:");
    Console.WriteLine(publicKey);
    return 0;
}

static int Sign(Dictionary<string, string> options)
{
    var packages = Require(options, "packages");
    var keyFile = Require(options, "key");
    if (!Directory.Exists(packages)) throw new ArgumentException($"Packages folder {packages} does not exist.");

    var pem = File.ReadAllText(keyFile);
    var password = ManifestSigning.IsEncrypted(pem)
        ? Environment.GetEnvironmentVariable(PasswordVariable) ?? ReadPassword("Key password: ")
        : string.Empty;
    using var key = ManifestSigning.ImportPrivateKey(pem, password);

    var result = ManifestBuilder.Build(new FileSystem(), packages, DateTimeOffset.UtcNow);
    foreach (var name in result.Ignored)
        Console.Error.WriteLine($"Ignored {name}: not named like {PackageFileName.Format("launcher", "1.2.0", "win-x64")}");
    if (result.Manifest.Packages.Count == 0)
        throw new ArgumentException($"No packages found in {packages}.");

    var manifestBytes = result.Manifest.ToJsonBytes();
    File.WriteAllBytes(Path.Combine(packages, PackageManifest.ManifestFileName), manifestBytes);
    File.WriteAllText(Path.Combine(packages, PackageManifest.SignatureFileName), ManifestSigning.Sign(manifestBytes, key));

    foreach (var p in result.Manifest.Packages)
        Console.WriteLine($"{p.Role,-9} {p.Version,-10} {p.Rid,-10} {p.Size,12:N0} bytes  {p.File}");
    Console.WriteLine($"Signed {result.Manifest.Packages.Count} package(s). Upload the folder's zips, {PackageManifest.ManifestFileName} and {PackageManifest.SignatureFileName} to the server.");
    return 0;
}

static int Verify(Dictionary<string, string> options)
{
    var packages = Require(options, "packages");
    var pub = Require(options, "pub");
    var publicKey = File.Exists(pub) ? File.ReadAllText(pub) : pub;

    var manifestBytes = File.ReadAllBytes(Path.Combine(packages, PackageManifest.ManifestFileName));
    var signature = File.ReadAllText(Path.Combine(packages, PackageManifest.SignatureFileName));
    if (!ManifestSigning.Verify(manifestBytes, signature, [publicKey]))
    {
        Console.Error.WriteLine("Signature does NOT match.");
        return 1;
    }

    Console.WriteLine("Signature is valid.");
    return 0;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--") || i + 1 >= args.Length)
            throw new ArgumentException($"Unexpected argument '{args[i]}'.");
        options[args[i][2..]] = args[++i];
    }
    return options;
}

static string Require(Dictionary<string, string> options, string name) =>
    options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Missing --{name}.");

static string ReadNewPassword()
{
    var first = ReadPassword("New key password (empty for none): ");
    if (first.Length == 0) return first;
    if (ReadPassword("Repeat password: ") != first) throw new ArgumentException("Passwords didn't match.");
    return first;
}

static string ReadPassword(string prompt)
{
    if (Console.IsInputRedirected)
        throw new ArgumentException($"Set {PasswordVariable} when input is redirected.");

    Console.Error.Write(prompt);
    var password = new StringBuilder();
    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
        else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
    }
    Console.Error.WriteLine();
    return password.ToString();
}
