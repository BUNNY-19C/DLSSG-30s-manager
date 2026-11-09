using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DLSSGManager.Tests;

public class ProjectSigningTests
{
    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    [Theory]
    [InlineData("CN=DLSSG Throwaway Test")]
    [InlineData("CN=DLSSG")]
    [InlineData("CN=Totally Not DLSSG Tool")]
    public void Certificate_named_like_the_project_is_not_the_project(string subject)
    {
        using var impostor = SelfSigned(subject);

        Assert.False(ProjectSigning.IsPinned(impostor));
        Assert.False(ProjectSigning.IsProjectSignature(impostor, signatureIntact: true));
    }

    [Fact]
    public void Missing_certificate_is_not_the_project() =>
        Assert.False(ProjectSigning.IsProjectSignature(null, signatureIntact: true));

    [Fact]
    public void Unsigned_file_is_not_project_signed()
    {
        using var dir = new TempDir();
        var dll = dir.File("version.dll");
        File.WriteAllBytes(dll, RandomNumberGenerator.GetBytes(4096));

        Assert.False(DeploymentService.IsProjectSigned(dll));
        Assert.False(DeploymentService.IsOurs(dll, recordedHash: null));
    }

    [Fact]
    public void File_is_ours_only_when_recorded_hash_matches()
    {
        using var dir = new TempDir();
        var dll = dir.File("dxgi.dll");
        File.WriteAllBytes(dll, RandomNumberGenerator.GetBytes(4096));
        var hash = DeploymentService.Sha256(dll);

        Assert.True(DeploymentService.IsOurs(dll, hash));
        Assert.False(DeploymentService.IsOurs(dll, new string('0', 64)));
    }
}
