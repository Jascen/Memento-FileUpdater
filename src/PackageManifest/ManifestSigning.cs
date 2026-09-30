using System.Security.Cryptography;

namespace FileUpdaterPackages;

//ECDSA P-256 signing of the manifest's exact bytes. The private key stays on the admin's machine;
//the launcher is built with the public key(s) and refuses a manifest none of them signed.
//Public keys are passed around as base64 of the SubjectPublicKeyInfo, signatures as base64 text.
public static class ManifestSigning
{
    private const string EncryptedPemHeader = "ENCRYPTED PRIVATE KEY";

    public static ECDsa GenerateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public static string ExportPublicKey(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    //An empty password leaves the key unencrypted
    public static string ExportPrivateKey(ECDsa key, string password) =>
        password.Length == 0
            ? key.ExportPkcs8PrivateKeyPem()
            : key.ExportEncryptedPkcs8PrivateKeyPem(password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 210_000));

    public static ECDsa ImportPrivateKey(string pem, string password)
    {
        var key = ECDsa.Create();
        if (pem.Contains(EncryptedPemHeader)) key.ImportFromEncryptedPem(pem, password);
        else key.ImportFromPem(pem);
        return key;
    }

    public static bool IsEncrypted(string pem) => pem.Contains(EncryptedPemHeader);

    public static string Sign(byte[] manifest, ECDsa privateKey) =>
        Convert.ToBase64String(privateKey.SignData(manifest, HashAlgorithmName.SHA256));

    //True when any of the trusted keys signed exactly these bytes. Malformed keys or signatures count as a failure
    public static bool Verify(byte[] manifest, string signatureBase64, IEnumerable<string> trustedPublicKeys)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var publicKey in trustedPublicKeys)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
                if (key.VerifyData(manifest, signature, HashAlgorithmName.SHA256)) return true;
            }
            catch (Exception e) when (e is FormatException or CryptographicException)
            {
                //A bad key in the list must not stop the others being tried
            }
        }

        return false;
    }
}
