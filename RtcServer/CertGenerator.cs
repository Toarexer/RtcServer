using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RtcServer;

internal static class CertGenerator {
	private const X509KeyUsageFlags KeyUsages = X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature;

	/// <summary>Creates a new <see cref="X509Certificate2"/> using ECDsa nistP256 encryption.</summary>
	public static X509Certificate2 Create() {
		using ECDsa ecDsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

		CertificateRequest request = new($"CN={nameof(RtcServer)}", ecDsa, HashAlgorithmName.SHA256);
		request.CertificateExtensions.Add(new X509KeyUsageExtension(KeyUsages, true));
		request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.5")], true));
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));

		return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
	}
}
