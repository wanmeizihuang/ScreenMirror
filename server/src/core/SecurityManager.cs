using System.Security.Cryptography;
using ScreenMirror.Protocol;

namespace ScreenMirror.Server.Core;

/// <summary>
/// 安全加密管理器 — ECDH 密钥交换 + AES-128-GCM 加密
/// </summary>
public class SecurityManager : IDisposable
{
    private ECDiffieHellman? _ecdh;
    private byte[]? _sharedKey;
    private readonly object _lock = new();

    public bool IsEncrypted => _sharedKey != null;
    public byte[]? PublicKey { get; private set; }

    /// <summary>
    /// 生成 ECDH 密钥对
    /// </summary>
    public byte[] GenerateKeyPair()
    {
        _ecdh?.Dispose();
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        return PublicKey;
    }

    /// <summary>
    /// 用对方的公钥派生共享密钥
    /// </summary>
    public void DeriveSharedKey(byte[] peerPublicKey)
    {
        if (_ecdh == null)
            throw new InvalidOperationException("Key pair not generated");

        using var peerKey = ECDiffieHellman.Create();
        peerKey.ImportSubjectPublicKeyInfo(peerPublicKey, out _);

        var rawShared = _ecdh.DeriveKeyMaterial(peerKey.PublicKey);

        // HKDF 派生 AES-128 密钥 (128 bits = 16 bytes)
        _sharedKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            rawShared,
            outputLength: 16,
            salt: null,
            info: System.Text.Encoding.UTF8.GetBytes("ScreenMirror-AES-128-GCM")
        );
    }

    /// <summary>
    /// 生成 4 位随机配对码
    /// </summary>
    public static string GeneratePairingCode()
    {
        return RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
    }

    /// <summary>
    /// AES-128-GCM 加密
    /// </summary>
    /// <returns>Nonce(12) + Ciphertext + Tag(16)</returns>
    public byte[] Encrypt(byte[] plaintext)
    {
        if (_sharedKey == null)
            throw new InvalidOperationException("Shared key not derived");

        lock (_lock)
        {
            var nonce = new byte[12]; // AES-GCM 推荐 12 字节 nonce
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];

            using var aes = new AesGcm(_sharedKey, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);

            // 组合: Nonce + Ciphertext + Tag
            var result = new byte[nonce.Length + ciphertext.Length + tag.Length];
            Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, nonce.Length + ciphertext.Length, tag.Length);
            return result;
        }
    }

    /// <summary>
    /// AES-128-GCM 解密
    /// </summary>
    public byte[] Decrypt(byte[] encryptedData)
    {
        if (_sharedKey == null)
            throw new InvalidOperationException("Shared key not derived");

        lock (_lock)
        {
            int nonceLen = 12;
            int tagLen = 16;
            int cipherLen = encryptedData.Length - nonceLen - tagLen;

            var nonce = encryptedData[..nonceLen];
            var ciphertext = encryptedData[nonceLen..(nonceLen + cipherLen)];
            var tag = encryptedData[(nonceLen + cipherLen)..];

            var plaintext = new byte[cipherLen];

            using var aes = new AesGcm(_sharedKey, tagLen);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }
    }

    /// <summary>
    /// 重置会话密钥
    /// </summary>
    public void Reset()
    {
        _sharedKey = null;
    }

    public void Dispose()
    {
        _ecdh?.Dispose();
        _ecdh = null;
        _sharedKey = null;
        PublicKey = null;
    }
}

/// <summary>
/// 加密连接包装器 — 在 ControlConnection 基础上添加加密层
/// </summary>
public class SecureConnection
{
    private readonly ControlConnection _inner;
    private readonly SecurityManager _security;

    public ControlConnection InnerConnection => _inner;
    public bool IsEncrypted => _security.IsEncrypted;

    public SecureConnection(ControlConnection connection, SecurityManager security)
    {
        _inner = connection;
        _security = security;
    }

    /// <summary>
    /// 发送加密消息
    /// </summary>
    public async Task SendEncryptedAsync(byte[] data)
    {
        if (_security.IsEncrypted)
        {
            data = _security.Encrypt(data);
        }
        await _inner.SendAsync(data);
    }

    /// <summary>
    /// 解密收到的消息
    /// </summary>
    public byte[] Decrypt(byte[] data)
    {
        return _security.IsEncrypted ? _security.Decrypt(data) : data;
    }

    /// <summary>
    /// 安全握手流程
    /// </summary>
    public static async Task<bool> PerformHandshake(
        ControlConnection connection,
        SecurityManager security,
        CancellationToken ct = default)
    {
        // 1. 生成密钥对
        var publicKey = security.GenerateKeyPair();

        // 2. 发送公钥给对端 (使用 DeviceHello 的扩展字段)
        // 实际实现中需要扩展协议或在握手后交换密钥
        // 此处为框架代码

        // 3. 接收对端公钥并派生共享密钥
        // security.DeriveSharedKey(peerPublicKey);

        // 4. 生成配对码确认
        var pairingCode = SecurityManager.GeneratePairingCode();
        // 显示配对码，用户确认后完成握手

        await Task.CompletedTask;
        return true;
    }
}
