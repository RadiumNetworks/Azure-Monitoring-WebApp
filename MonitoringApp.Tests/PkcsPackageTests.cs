using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MonitoringApp.Tests;

public sealed class PkcsPackageTests
{
    [Fact]
    public void SignsAndVerifiesDetachedCmsWithUpdatedPackage()
    {
        using var key = RSA.Create(2048);
        using var certificate = CreateCertificate(key);
        var content = Encoding.UTF8.GetBytes("MonitoringApp PKCS signature compatibility");
        var signed = new SignedCms(new ContentInfo(content), detached: true);
        signed.ComputeSignature(new CmsSigner(certificate));

        var decoded = new SignedCms(new ContentInfo(content), detached: true);
        decoded.Decode(signed.Encode());
        decoded.CheckSignature(verifySignatureOnly: true);
        Assert.Single(decoded.SignerInfos);
        Assert.Equal(content, decoded.ContentInfo.Content);
    }

    [Fact]
    public void EncryptsDecodesAndDecryptsCmsWithUpdatedPackage()
    {
        using var key = RSA.Create(2048);
        using var certificate = CreateCertificate(key);
        var content = Encoding.UTF8.GetBytes("MonitoringApp PKCS encryption compatibility");
        var envelope = new EnvelopedCms(new ContentInfo(content));
        envelope.Encrypt(new CmsRecipient(certificate));

        var decoded = new EnvelopedCms();
        decoded.Decode(envelope.Encode());
        decoded.Decrypt(new X509Certificate2Collection(certificate));
        Assert.Equal(content, decoded.ContentInfo.Content);
    }

    private static X509Certificate2 CreateCertificate(RSA key) =>
        new CertificateRequest("CN=MonitoringApp-PKCS-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
}
