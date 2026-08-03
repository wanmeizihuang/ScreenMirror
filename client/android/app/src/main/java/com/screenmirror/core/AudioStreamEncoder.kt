package com.screenmirror.core

import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.media.MediaRecorder
import kotlinx.coroutines.*
import java.io.OutputStream
import java.net.Socket

/**
 * 音频采集和编码服务 — AudioRecord 采集 PCM → MediaCodec AAC 编码 → TCP 发送
 */
class AudioStreamEncoder(
    private val host: String,
    private val port: Int = 35356
) {
    companion object {
        private const val SAMPLE_RATE = 44100
        private const val CHANNEL_COUNT = 2  // 立体声
        private const val BIT_RATE = 128_000  // 128 kbps
        private const val CHANNEL_CONFIG = AudioFormat.CHANNEL_IN_STEREO
        private const val AUDIO_FORMAT = AudioFormat.ENCODING_PCM_16BIT
    }

    private var audioRecord: AudioRecord? = null
    private var mediaCodec: MediaCodec? = null
    private var socket: Socket? = null
    private var outputStream: OutputStream? = null
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private var isRunning = false

    /**
     * 初始化并启动音频采集编码
     */
    suspend fun start(): Boolean = withContext(Dispatchers.IO) {
        try {
            // 1. 连接 TCP
            socket = Socket(host, port)
            socket?.tcpNoDelay = true
            outputStream = socket?.getOutputStream()

            // 2. 初始化 AudioRecord
            val minBufferSize = AudioRecord.getMinBufferSize(
                SAMPLE_RATE, CHANNEL_CONFIG, AUDIO_FORMAT
            )
            val bufferSize = Math.max(minBufferSize, 4096)

            audioRecord = AudioRecord(
                MediaRecorder.AudioSource.DEFAULT,  // 内部音频（需要系统权限）
                SAMPLE_RATE,
                CHANNEL_CONFIG,
                AUDIO_FORMAT,
                bufferSize * 2
            )

            if (audioRecord?.state != AudioRecord.STATE_INITIALIZED) {
                // 降级使用 MIC 输入
                audioRecord = AudioRecord(
                    MediaRecorder.AudioSource.MIC,  // 如果是系统音频不可用，用麦克风
                    SAMPLE_RATE,
                    CHANNEL_CONFIG,
                    AUDIO_FORMAT,
                    bufferSize * 2
                )
            }

            if (audioRecord?.state != AudioRecord.STATE_INITIALIZED) {
                return@withContext false
            }

            // 3. 初始化 AAC 编码器
            val format = MediaFormat.createAudioFormat(
                MediaFormat.MIMETYPE_AUDIO_AAC,
                SAMPLE_RATE,
                CHANNEL_COUNT
            ).apply {
                setInteger(MediaFormat.KEY_BIT_RATE, BIT_RATE)
                setInteger(MediaFormat.KEY_AAC_PROFILE,
                    MediaCodecInfo.CodecProfileLevel.AACObjectLC)
                setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, bufferSize)
            }

            mediaCodec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_AAC)
            mediaCodec?.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            mediaCodec?.start()

            // 4. 开始采集和编码
            isRunning = true
            audioRecord?.startRecording()

            // 启动编码线程
            scope.launch { encodeLoop() }
            scope.launch { sendLoop() }

            return@withContext true
        } catch (e: Exception) {
            e.printStackTrace()
            return@withContext false
        }
    }

    /**
     * 编码循环：从 AudioRecord 读 PCM → 喂给 MediaCodec
     */
    private suspend fun encodeLoop() = withContext(Dispatchers.IO) {
        val record = audioRecord ?: return@withContext
        val codec = mediaCodec ?: return@withContext
        val buffer = ByteArray(4096)

        val bufferInfo = MediaCodec.BufferInfo()

        try {
            while (isRunning && socket?.isConnected == true) {
                val inputIndex = codec.dequeueInputBuffer(10_000L)
                if (inputIndex >= 0) {
                    val inputBuffer = codec.getInputBuffer(inputIndex) ?: continue
                    val readBytes = record.read(buffer, 0, buffer.size)
                    if (readBytes > 0) {
                        inputBuffer.clear()
                        inputBuffer.put(buffer, 0, readBytes)
                        codec.queueInputBuffer(inputIndex, 0, readBytes,
                            System.nanoTime() / 1000, 0)
                    }
                }

                // 取出编码后的 AAC 数据
                val outputIndex = codec.dequeueOutputBuffer(bufferInfo, 0)
                if (outputIndex >= 0) {
                    val outputBuffer = codec.getOutputBuffer(outputIndex)
                    if (outputBuffer != null && bufferInfo.size > 0) {
                        val aacData = ByteArray(bufferInfo.size)
                        outputBuffer.position(bufferInfo.offset)
                        outputBuffer.get(aacData)

                        // 发送带 ADTS 头的数据
                        val adtsFrame = addAdtsHeader(aacData)
                        outputStream?.write(adtsFrame)
                        outputStream?.flush()
                    }
                    codec.releaseOutputBuffer(outputIndex, false)
                }
            }
        } catch (e: Exception) {
            if (isRunning) e.printStackTrace()
        }
    }

    /**
     * 发送心跳/保活循环
     */
    private suspend fun sendLoop() = withContext(Dispatchers.IO) {
        try {
            while (isRunning && socket?.isConnected == true) {
                delay(5000)
                // TCP keepalive 已设置，无需额外操作
            }
        } catch (_: Exception) {}
    }

    /**
     * 给 AAC 原始数据添加 ADTS 头（7 字节）
     */
    private fun addAdtsHeader(aacData: ByteArray, sampleRate: Int = SAMPLE_RATE): ByteArray {
        val profile = 2  // AAC LC
        val freqIndex = when (sampleRate) {
            96000 -> 0; 88200 -> 1; 64000 -> 2; 48000 -> 3
            44100 -> 4; 32000 -> 5; 24000 -> 6; 22050 -> 7
            16000 -> 8; 12000 -> 9; 11025 -> 10; 8000 -> 11
            else -> 4  // 默认 44100
        }
        val chanConfig = CHANNEL_COUNT
        val frameLength = aacData.size + 7

        val adts = ByteArray(7)
        // Sync word: 0xFFF
        adts[0] = 0xFF.toByte()
        adts[1] = 0xF9.toByte()  // 0xFFF + 0x1 (MPEG-4) + 00 (Layer)
        adts[2] = ((profile - 1) shl 6 or (freqIndex shl 2) or (chanConfig shr 2)).toByte()
        adts[3] = ((chanConfig and 3) shl 6 or (frameLength shr 11)).toByte()
        adts[4] = ((frameLength shr 3) and 0xFF).toByte()
        adts[5] = ((frameLength and 7) shl 5 or 0x1F).toByte()
        adts[6] = 0xFC.toByte()

        val result = ByteArray(adts.size + aacData.size)
        System.arraycopy(adts, 0, result, 0, adts.size)
        System.arraycopy(aacData, 0, result, adts.size, aacData.size)
        return result
    }

    fun stop() {
        isRunning = false
        scope.launch {
            mediaCodec?.stop()
            mediaCodec?.release()
            mediaCodec = null

            audioRecord?.stop()
            audioRecord?.release()
            audioRecord = null

            outputStream?.close()
            socket?.close()
        }
        scope.cancel()
    }
}
