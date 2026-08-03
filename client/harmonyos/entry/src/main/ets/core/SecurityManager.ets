// SecurityManager.ets — ECDH + AES-128-GCM 加密工具
// 对应 Android 端 SecurityManager.kt 和 Windows 端 SecurityManager.cs
// ⚠️ 当前在连接流程中未接线，仅保留算法实现供后续集成

import { cryptoFramework } from '@kit.CryptoArchitectureKit'
import { buffer } from '@kit.ArkTS'

export class SecurityManager {
  private keyPair: cryptoFramework.KeyPair | null = null
  private sharedKey: Uint8Array | null = null
  private keyAgreement: cryptoFramework.KeyAgreement | null = null
  private cipher: cryptoFramework.Cipher | null = null

  static readonly GCM_NONCE_LENGTH: number = 12
  static readonly GCM_TAG_LENGTH: number = 16
  static readonly AES_KEY_LENGTH: number = 16 // 128-bit

  /** 生成 ECDH P-256 密钥对 */
  async generateKeyPair(): Promise<cryptoFramework.KeyPair> {
    try {
      const generator = cryptoFramework.createAsyKeyGenerator('ECC256')
      this.keyPair = await generator.generateKeyPair()
      return this.keyPair
    } catch (err) {
      console.error(`[Security] generateKeyPair error: ${JSON.stringify(err)}`)
      throw err
    }
  }

  /** 导出公钥（SubjectPublicKeyInfo 格式，与 C# ECDiffieHellman.ExportSubjectPublicKeyInfo 对齐） */
  async getPublicKey(): Promise<Uint8Array> {
    if (!this.keyPair) {
      throw new Error('Key pair not generated')
    }
    return new Uint8Array(this.keyPair.pubKey.getEncoded())
  }

  /** 派生共享密钥（HKDF-SHA256 简化版） */
  async deriveSharedKey(peerPublicKey: Uint8Array): Promise<Uint8Array> {
    try {
      // ECDH 密钥协商
      const keyGen = cryptoFramework.createAsyKeyGenerator('ECC256')
      const pubKey = await keyGen.convertKey({ data: peerPublicKey.buffer as ArrayBuffer }, null)

      this.keyAgreement = cryptoFramework.createKeyAgreement('ECC256')
      const sharedSecret = await this.keyAgreement.generateSecret(this.keyPair!.priKey, pubKey.pubKey!)

      // HKDF 简化: SHA-256(sharedSecret)，取前 16 字节
      const md = cryptoFramework.createMd('SHA256')
      const digest = await md.digest({ data: sharedSecret.data })
      this.sharedKey = new Uint8Array(digest.data.slice(0, SecurityManager.AES_KEY_LENGTH))

      console.info(`[Security] Shared key derived: ${this.sharedKey.length} bytes`)
      return this.sharedKey

    } catch (err) {
      console.error(`[Security] deriveSharedKey error: ${JSON.stringify(err)}`)
      throw err
    }
  }

  /** AES-128-GCM 加密 */
  async encrypt(plaintext: Uint8Array): Promise<Uint8Array> {
    if (!this.sharedKey) throw new Error('Shared key not derived')

    try {
      // 生成随机 Nonce
      const nonce = cryptoFramework.createRandom().generateRandom(SecurityManager.GCM_NONCE_LENGTH)

      // AES-GCM 加密
      const symKeyGen = cryptoFramework.createSymKeyGenerator('AES128')
      const symKey = await symKeyGen.convertKey({ data: this.sharedKey.buffer as ArrayBuffer })
      this.cipher = cryptoFramework.createCipher('AES128|GCM|PKCS5')
      await this.cipher.init(cryptoFramework.CryptoMode.ENCRYPT_MODE, symKey, {
        data: new Uint8Array(0) // IV 在 init 后设置
      })

      const encrypted = await this.cipher.doFinal({ data: plaintext.buffer as ArrayBuffer })

      // 输出: Nonce(12) + Ciphertext + Tag(16)
      const result = new Uint8Array(SecurityManager.GCM_NONCE_LENGTH + encrypted.data.byteLength)
      result.set(nonce.data, 0)
      result.set(new Uint8Array(encrypted.data), SecurityManager.GCM_NONCE_LENGTH)

      return result
    } catch (err) {
      console.error(`[Security] encrypt error: ${JSON.stringify(err)}`)
      throw err
    }
  }

  /** AES-128-GCM 解密 */
  async decrypt(encrypted: Uint8Array): Promise<Uint8Array> {
    if (!this.sharedKey) throw new Error('Shared key not derived')

    try {
      // 拆分 Nonce
      const nonce = encrypted.slice(0, SecurityManager.GCM_NONCE_LENGTH)
      const data = encrypted.slice(SecurityManager.GCM_NONCE_LENGTH)

      const symKeyGen = cryptoFramework.createSymKeyGenerator('AES128')
      const symKey = await symKeyGen.convertKey({ data: this.sharedKey.buffer as ArrayBuffer })
      this.cipher = cryptoFramework.createCipher('AES128|GCM|PKCS5')
      await this.cipher.init(cryptoFramework.CryptoMode.DECRYPT_MODE, symKey, {
        data: new Uint8Array(0)
      })

      const decrypted = await this.cipher.doFinal({ data: data.buffer as ArrayBuffer })
      return new Uint8Array(decrypted.data)
    } catch (err) {
      console.error(`[Security] decrypt error: ${JSON.stringify(err)}`)
      throw err
    }
  }

  /** 生成 4 位配对码 */
  generatePairingCode(): string {
    const rand = cryptoFramework.createRandom()
    const bytes = rand.generateRandom(2).data
    const num = new Uint8Array(bytes)[0] % 10000
    return num.toString().padStart(4, '0')
  }
}
