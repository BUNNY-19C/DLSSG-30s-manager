using System.Security.Cryptography.X509Certificates;

namespace DLSSGManager;

/// <summary>
/// Which certificates identify a build of the upstream project.
///
/// Identity is decided by thumbprint, never by subject: anyone can create a self-signed certificate
/// named "CN=DLSSG …", and ownership checks built on that name would let such a file be overwritten
/// without a backup or deleted on restore as if this project had written it.
/// </summary>
public static class ProjectSigning
{
    /// <summary>
    /// Known project signing certificates. The 0.4.1 Release archive and the official 0.4.1 tag's
    /// version.dll both use CN=DLSSG Throwaway Test, D963A96D623B85F67B7B323BEB353CEE90225D40
    /// (verified 2026-10-27). The previous certificate is retained for older payloads that existing
    /// installations still carry.
    /// </summary>
    public static IReadOnlyList<string> PinnedThumbprints { get; } = new[]
    {
        "85BA66762F851E49148D706915D09026281418E6",
        "D963A96D623B85F67B7B323BEB353CEE90225D40",
    };

    /// <summary>True when the certificate is one of the pinned project certificates.</summary>
    public static bool IsPinned(X509Certificate2? certificate) =>
        certificate is not null &&
        PinnedThumbprints.Contains(certificate.Thumbprint, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a file's signature is intact and was made with a pinned project certificate. Both
    /// conditions are required: a pinned certificate on a tampered file proves nothing.
    /// </summary>
    public static bool IsProjectSignature(X509Certificate2? certificate, bool signatureIntact) =>
        signatureIntact && IsPinned(certificate);
}
