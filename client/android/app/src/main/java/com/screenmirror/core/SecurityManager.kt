package com.screenmirror.core

import java.security.KeyPairGenerator
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * 安全加密管理器 — ECDH 密钥交换 + AES-128-GCM
 */
class SecurityManager {

    companion object {
        private const val EC_CURVE = "secp256r1"
        private const val AES_ALGORITHM = "AES/GCM/NoPadding"
        private const val KEY_SIZE = 128
        private const val GCM_NONCE_LENGTH = 12
        private const val GCM_TAG_LENGTH = 128
    }

    private var keyAgreement: KeyAgreement? = null
    private var sharedKey: ByteArray? = null

    val publicKey: ByteArray
        get() = keyPair?.public?.encoded ?: ByteArray(0)

    val isEncrypted: Boolean
        get() = sharedKey != null

    private var keyPair: java.security.KeyPair? = null

    /**
     * 生成 ECDH 密钥对
     */
    fun generateKeyPair(): ByteArray {
        val generator = KeyPairGenerator.getInstance("EC")
        generator.initialize(ECGenParameterSpec(EC_CURVE))
        keyPair = generator.generateKeyPair()

        keyAgreement = KeyAgreement.getInstance("ECDH")
        keyAgreement?.init(keyPair!!.private)

        return keyPair!!.public.encoded
    }

    /**
     * 用对方公钥派生共享密钥
     */
    fun deriveSharedKey(peerPublicKey: ByteArray) {
        val keyFactory = java.security.KeyFactory.getInstance("EC")
        val pubKeySpec = java.security.spec.X509EncodedKeySpec(peerPublicKey)
        val pubKey = keyFactory.generatePublic(pubKeySpec)

        keyAgreement?.doPhase(pubKey, true)
        val sharedSecret = keyAgreement?.generateSecret()

        // 使用 HKDF 简化版本: SHA-256 → 取前 16 字节
        val digest = java.security.MessageDigest.getInstance("SHA-256")
        val hkdf = digest.digest(sharedSecret)
        sharedKey = hkdf.copyOf(KEY_SIZE / 8)  // 16 bytes for AES-128
    }

    /**
     * 生成 4 位随机配对码
     */
    fun generatePairingCode(): String {
        val code = SecureRandom().nextInt(10000)
        return String.format("%04d", code)
    }

    /**
     * AES-128-GCM 加密
     * @return Nonce(12) + Ciphertext + Tag(16)
     */
    fun encrypt(plaintext: ByteArray): ByteArray {
        val key = sharedKey ?: throw IllegalStateException("Shared key not derived")

        val nonce = ByteArray(GCM_NONCE_LENGTH)
        SecureRandom().nextBytes(nonce)

        val cipher = Cipher.getInstance(AES_ALGORITHM)
        val keySpec = SecretKeySpec(key, "AES")
        val gcmSpec = GCMParameterSpec(GCM_TAG_LENGTH, nonce)
        cipher.init(Cipher.ENCRYPT_MODE, keySpec, gcmSpec)

        val ciphertext = cipher.doFinal(plaintext)

        // 组合: Nonce + Ciphertext (含 Tag)
        val result = ByteArray(nonce.size + ciphertext.size)
        System.arraycopy(nonce, 0, result, 0, nonce.size)
        System.arraycopy(ciphertext, 0, result, nonce.size, ciphertext.size)
        return result
    }

    /**
     * AES-128-GCM 解密
     */
    fun decrypt(encryptedData: ByteArray): ByteArray {
        val key = sharedKey ?: throw IllegalStateException("Shared key not derived")

        val nonce = encryptedData.copyOfRange(0, GCM_NONCE_LENGTH)
        val ciphertext = encryptedData.copyOfRange(GCM_NONCE_LENGTH, encryptedData.size)

        val cipher = Cipher.getInstance(AES_ALGORITHM)
        val keySpec = SecretKeySpec(key, "AES")
        val gcmSpec = GCMParameterSpec(GCM_TAG_LENGTH, nonce)
        cipher.init(Cipher.DECRYPT_MODE, keySpec, gcmSpec)

        return cipher.doFinal(ciphertext)
    }

    /**
     * 重置会话
     */
    fun reset() {
        sharedKey = null
        keyAgreement = null
        keyPair = null
    }
}
