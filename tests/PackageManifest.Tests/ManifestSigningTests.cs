using System.Security.Cryptography;
using System.Text;

namespace FileUpdaterPackages.Tests;

public class ManifestSigningTests
{
    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("""{"packages":[]}""");

    [Fact]
    public void AcceptsAManifestSignedByATrustedKey()
    {
        //Arrange
        using var key = ManifestSigning.GenerateKey();
        var signature = ManifestSigning.Sign(Manifest, key);

        //Act
        var valid = ManifestSigning.Verify(Manifest, signature, [ManifestSigning.ExportPublicKey(key)]);

        //Assert
        Assert.True(valid);
    }

    [Fact]
    public void RejectsAManifestChangedAfterSigning()
    {
        //Arrange
        using var key = ManifestSigning.GenerateKey();
        var signature = ManifestSigning.Sign(Manifest, key);
        var tampered = Encoding.UTF8.GetBytes("""{"packages":[{}]}""");

        //Act
        var valid = ManifestSigning.Verify(tampered, signature, [ManifestSigning.ExportPublicKey(key)]);

        //Assert
        Assert.False(valid);
    }

    [Fact]
    public void RejectsAManifestSignedByAnUntrustedKey()
    {
        //Arrange
        using var attacker = ManifestSigning.GenerateKey();
        using var real = ManifestSigning.GenerateKey();
        var signature = ManifestSigning.Sign(Manifest, attacker);

        //Act
        var valid = ManifestSigning.Verify(Manifest, signature, [ManifestSigning.ExportPublicKey(real)]);

        //Assert
        Assert.False(valid);
    }

    [Fact]
    public void AcceptsAnyOneOfSeveralTrustedKeys()
    {
        //Arrange
        using var oldKey = ManifestSigning.GenerateKey();
        using var newKey = ManifestSigning.GenerateKey();
        var signature = ManifestSigning.Sign(Manifest, newKey);

        //Act
        var valid = ManifestSigning.Verify(Manifest, signature,
            ["not a key", ManifestSigning.ExportPublicKey(oldKey), ManifestSigning.ExportPublicKey(newKey)]);

        //Assert
        Assert.True(valid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!!")]
    [InlineData("AAAA")] //Valid base64, wrong signature
    public void TreatsMalformedSignaturesAsInvalid(string signature)
    {
        //Arrange
        using var key = ManifestSigning.GenerateKey();

        //Act
        var valid = ManifestSigning.Verify(Manifest, signature, [ManifestSigning.ExportPublicKey(key)]);

        //Assert
        Assert.False(valid);
    }

    [Fact]
    public void NoTrustedKeysMeansNothingVerifies()
    {
        //Arrange
        using var key = ManifestSigning.GenerateKey();
        var signature = ManifestSigning.Sign(Manifest, key);

        //Act
        var valid = ManifestSigning.Verify(Manifest, signature, []);

        //Assert
        Assert.False(valid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("correct horse")]
    public void PrivateKeyRoundTripsThroughPemWithAndWithoutAPassword(string password)
    {
        //Arrange
        using var key = ManifestSigning.GenerateKey();
        var pem = ManifestSigning.ExportPrivateKey(key, password);

        //Act
        using var imported = ManifestSigning.ImportPrivateKey(pem, password);

        //Assert
        Assert.Equal(password.Length > 0, ManifestSigning.IsEncrypted(pem));
        Assert.True(ManifestSigning.Verify(Manifest, ManifestSigning.Sign(Manifest, imported), [ManifestSigning.ExportPublicKey(key)]));
    }

    [Fact]
    public void WrongPasswordCantOpenTheKey()
    {
        //Arrange
        using var key = ManifestSigning.GenerateKey();
        var pem = ManifestSigning.ExportPrivateKey(key, "right");

        //Act
        var open = () => ManifestSigning.ImportPrivateKey(pem, "wrong");

        //Assert
        Assert.ThrowsAny<CryptographicException>(open);
    }
}
