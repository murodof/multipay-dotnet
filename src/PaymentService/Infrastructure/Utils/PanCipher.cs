using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace PaymentService.Infrastructure.Utils;

public sealed class PanCipher(byte[] key)
{
    public static PanCipher? From(string value) =>
        Core.Configurations.Settings.PanEncryptionKey(value) is { } key ? new PanCipher(key) : null;

    public string Encrypt(string plaintext, string associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[data.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, data, ciphertext, tag, Encoding.UTF8.GetBytes(associatedData));
        return Base64Url.EncodeToString([.. nonce, .. ciphertext, .. tag]);
    }

    public string? Decrypt(string token, string associatedData)
    {
        byte[] raw;
        try
        {
            raw = Base64Url.DecodeFromChars(token);
        }
        catch (FormatException)
        {
            return null;
        }
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        if (raw.Length < nonceSize + tagSize)
        {
            return null;
        }
        var plaintext = new byte[raw.Length - nonceSize - tagSize];
        try
        {
            using var aes = new AesGcm(key, tagSize);
            aes.Decrypt(raw[..nonceSize], raw[nonceSize..^tagSize], raw[^tagSize..], plaintext, Encoding.UTF8.GetBytes(associatedData));
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        return Encoding.UTF8.GetString(plaintext);
    }
}
