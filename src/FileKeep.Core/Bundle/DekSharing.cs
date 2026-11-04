using System.Security.Cryptography;

namespace FileKeep.Core.Bundle;

/// <summary>
/// XOR secret sharing for AND-layered unlocks. A DEK is split into N
/// shares (N-1 random + 1 XOR-computed); all N are required to
/// reconstruct. Each share is wrapped with a different method's KEK.
/// Shared between the desktop exporter and the iOS secure workspace.
/// </summary>
public static class DekSharing
{
	/// <summary>
	/// Splits a 32-byte DEK into <paramref name="count"/> XOR shares.
	/// </summary>
	public static byte[][] Split(byte[] dek, int count)
	{
		ArgumentNullException.ThrowIfNull(dek);
		if (dek.Length != 32)
			throw new ArgumentException("DEK must be 32 bytes.", nameof(dek));
		if (count < 2)
			throw new ArgumentException("AND groups need at least 2 shares.", nameof(count));

		var shares = new byte[count][];
		for (int i = 0; i < count - 1; i++)
			shares[i] = RandomNumberGenerator.GetBytes(32);

		// Last share = DEK XOR all previous shares.
		var last = new byte[32];
		Array.Copy(dek, last, 32);
		for (int i = 0; i < count - 1; i++)
			for (int j = 0; j < 32; j++)
				last[j] ^= shares[i][j];
		shares[count - 1] = last;
		return shares;
	}

	/// <summary>
	/// Reconstructs the DEK by XORing all shares. All shares required.
	/// </summary>
	public static byte[] Combine(byte[][] shares)
	{
		ArgumentNullException.ThrowIfNull(shares);
		if (shares.Length < 2)
			throw new ArgumentException("Need at least 2 shares.", nameof(shares));
		foreach (var s in shares)
		{
			ArgumentNullException.ThrowIfNull(s);
			if (s.Length != 32)
				throw new ArgumentException("Each share must be 32 bytes.", nameof(shares));
		}

		var dek = new byte[32];
		foreach (var s in shares)
			for (int j = 0; j < 32; j++)
				dek[j] ^= s[j];
		return dek;
	}
}
