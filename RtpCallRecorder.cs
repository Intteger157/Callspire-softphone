using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NAudio.Codecs;
using SIPSorceryMedia.Abstractions;
using Concentus.Structs;

namespace Softphone
{
    /// <summary>
    /// Рекордер для записи звонков через RTP потоки
    /// Записывает inbound и outbound RTP в моно PCM файлы, затем микширует и конвертирует в WAV через ffmpeg
    /// </summary>
    public class RtpCallRecorder : CallRecorderBase
    {
        private const int SampleRate = 48000; // 48 kHz для Opus
        private const int FrameSize = SampleRate / 50; // 20ms фреймы: 48000 / 50 = 960 samples
        
        // RAW PCM файлы + ffmpeg конвертация
        private FileStream? _pcmInStream;  // Inbound (голос собеседника)
        private FileStream? _pcmOutStream;  // Outbound (микрофон)
        private string? _pcmInPath;
        private string? _pcmOutPath;
        
        // Счетчики для логирования
        private int _inboundRtpPacketsSeen = 0;
        private int _outboundPcmChunksSeen = 0;
        private int _outboundRtpPacketsSeen = 0;
        private int _outboundRtpPayloadProcessed = 0;
        
        // Opus декодер для inbound потока
        private OpusDecoder? _opusDecoderInbound;
        private readonly object _opusDecoderLock = new object();
        
        // Переопределяем RecordingFilePath для возврата финального MP3 файла
        public new string? RecordingFilePath => _recordingFilePath;

        /// <summary>
        /// Начинает запись звонка
        /// </summary>
        public override void StartRecording(string phoneNumber, DateTime callStartTime, string recordingsDirectory)
        {
            base.StartRecording(phoneNumber, callStartTime, recordingsDirectory);
            
            lock (_lockObject)
            {
                if (!_isRecording) return;

                try
                {
                    if (string.IsNullOrEmpty(_pcmInPath))
                    {
                        MainWindow.Log($"[RtpCallRecorder] ERROR: _pcmInPath is null or empty!");
                        _isRecording = false;
                        return;
                    }
                    
                    if (string.IsNullOrEmpty(_pcmOutPath))
                    {
                        MainWindow.Log($"[RtpCallRecorder] ERROR: _pcmOutPath is null or empty!");
                        _isRecording = false;
                        return;
                    }
                    
                    // Создаем FileStream для записи inbound RAW PCM (s16le, 48kHz, mono)
                    _pcmInStream = new FileStream(
                        _pcmInPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        useAsync: false);
                    
                    // Создаем FileStream для записи outbound RAW PCM (s16le, 48kHz, mono)
                    _pcmOutStream = new FileStream(
                        _pcmOutPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        useAsync: false);
                    
                    // Очищаем счетчики
                    _inboundRtpPacketsSeen = 0;
                    _outboundPcmChunksSeen = 0;
                    lock (_decodeLogLock)
                    {
                        _loggedPayloadIds.Clear();
                    }
                    
                    MainWindow.Log($"[RtpCallRecorder] PCM streams created: Inbound={_pcmInPath}, Outbound={_pcmOutPath} (s16le 48k mono)");
                    
                    // Self-test декодера μ-law для диагностики
                    short t1 = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(0xFF);
                    short t2 = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(0x7F);
                    short t3 = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(0x00);
                    MainWindow.Log($"[RtpCallRecorder] MuLaw selftest (NAudio.Codecs): 0xFF->{t1}, 0x7F->{t2}, 0x00->{t3}");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[RtpCallRecorder] Error starting recording: {ex.Message}");
                    _isRecording = false;
                    _pcmInPath = null;
                    _recordingFilePath = null;
                    _pcmInStream?.Dispose();
                    _pcmInStream = null;
                }
            }
        }

        /// <summary>
        /// Генерирует пути к файлам записи
        /// </summary>
        protected override void GenerateRecordingPaths(string baseFileName, string recordingsDirectory)
        {
            string pcmInBasePath = Path.Combine(recordingsDirectory, $"{baseFileName}_inbound.pcm");
            string pcmOutBasePath = Path.Combine(recordingsDirectory, $"{baseFileName}_outbound.pcm");
            // КРИТИЧНО: Используем WAV вместо MP3 для SIP звонков (единообразие с WebRTC, лучшая совместимость с AmoCRM)
            string wavBasePath = Path.Combine(recordingsDirectory, $"{baseFileName}.wav");
            
            // Генерируем уникальные пути
            _pcmInPath = GenerateUniqueFilePath(pcmInBasePath, path => File.Exists(path));
            _pcmOutPath = GenerateUniqueFilePath(pcmOutBasePath, path => File.Exists(path));
            _recordingFilePath = GenerateUniqueFilePath(wavBasePath, path => File.Exists(path));
            
            MainWindow.Log($"[RtpCallRecorder] Recording paths: PCM_In={_pcmInPath}, PCM_Out={_pcmOutPath}, WAV={_recordingFilePath}");
        }

        /// <summary>
        /// Возвращает префикс для логов
        /// </summary>
        protected override string GetLogPrefix() => "RtpCallRecorder";

        // УПРОЩЕННАЯ ЗАПИСЬ: убран EnsureWriterCreated, используется только FileStream
        
        /// <summary>
        /// Обрабатывает входящий RTP пакет (удаленная сторона)
        /// </summary>
        public void ProcessInboundRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload)
        {
            if (!_isRecording || _pcmInStream == null) return;
            
            // Инкрементируем счетчик RTP пакетов
            _inboundRtpPacketsSeen++;
            
            // Логируем первые несколько пакетов
            if (_inboundRtpPacketsSeen == 1)
            {
                MainWindow.Log($"[RtpCallRecorder] First inbound RTP packet from {remoteEndPoint}");
            }
            else if (_inboundRtpPacketsSeen <= 10 && _inboundRtpPacketsSeen % 5 == 0)
            {
                MainWindow.Log($"[RtpCallRecorder] Inbound RTP packet #{_inboundRtpPacketsSeen} from {remoteEndPoint}");
            }

            try
            {
                // Декодируем RTP payload в PCM (48kHz, 16-bit, mono)
                DecodedPcm decoded = DecodeRtpPayloadEx(payload, payloadID, SampleRate);
                if (decoded.Pcm48k == null || decoded.Pcm48k.Length == 0) return;
                
                // УПРОЩЕННАЯ ЗАПИСЬ: пишем decoded PCM напрямую в RAW PCM файл
                // decoded.Pcm48k уже в формате s16le, 48kHz, mono
                lock (_lockObject)
                {
                    if (_pcmInStream != null && _isRecording)
                    {
                        _pcmInStream.Write(decoded.Pcm48k, 0, decoded.Pcm48k.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RtpCallRecorder] Error processing inbound RTP: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes 48 kHz mono PCM samples (microphone, full bandwidth) into the outbound PCM file.
        /// No further resampling needed — data is already at recording sample rate.
        /// </summary>
        public void ProcessOutboundRawPcm48k(short[] pcm48k)
        {
            if (!_isRecording || _pcmOutStream == null) return;
            if (pcm48k == null || pcm48k.Length == 0) return;

            _outboundRtpPacketsSeen++;

            try
            {
                byte[] bytes = new byte[pcm48k.Length * 2];
                Buffer.BlockCopy(pcm48k, 0, bytes, 0, bytes.Length);

                lock (_lockObject)
                {
                    if (_pcmOutStream != null && _isRecording)
                    {
                        _pcmOutStream.Write(bytes, 0, bytes.Length);
                        _outboundRtpPayloadProcessed++;

                        if (_outboundRtpPacketsSeen == 1)
                        {
                            int peak = pcm48k.Max(s => Math.Abs((int)s));
                            long rms = 0;
                            for (int i = 0; i < pcm48k.Length; i++)
                                rms += (long)pcm48k[i] * pcm48k[i];
                            int rmsVal = (int)Math.Sqrt(rms / pcm48k.Length);
                            MainWindow.Log($"[RtpCallRecorder][OUT] First 48k PCM chunk: samples={pcm48k.Length}, " +
                                $"peak={peak}, rms={rmsVal}, bytes={bytes.Length}");
                        }
                        else if (_outboundRtpPacketsSeen % 50 == 0)
                        {
                            MainWindow.Log($"[RtpCallRecorder][OUT] 48k PCM chunks={_outboundRtpPacketsSeen}, " +
                                $"outbound bytes={_pcmOutStream.Position}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RtpCallRecorder][OUT] Error writing 48k PCM chunk #{_outboundRtpPacketsSeen}: {ex.Message}");
            }
        }

        /// <summary>
        /// [DEPRECATED] Обрабатывает исходящий RTP payload (закодированный).
        /// Оставлен для совместимости с WinMM fallback-путём.
        /// </summary>
        public void ProcessOutboundRtpPayload(byte payloadType, byte[] payload, int sourceRateHz)
        {
            if (!_isRecording || _pcmOutStream == null)
            {
                if (_outboundRtpPacketsSeen < 3)
                {
                    MainWindow.Log($"[RtpCallRecorder] ProcessOutboundRtpPayload called but not recording: isRecording={_isRecording}, pcmOutStream={_pcmOutStream == null}");
                }
                return;
            }
            
            if (payload == null || payload.Length == 0)
            {
                if (_outboundRtpPacketsSeen < 3)
                {
                    MainWindow.Log($"[RtpCallRecorder] ProcessOutboundRtpPayload called with empty payload");
                }
                return;
            }
            
            // Инкрементируем счетчик outbound RTP пакетов
            _outboundRtpPacketsSeen++;
            
            // Логируем первые несколько пакетов
            if (_outboundRtpPacketsSeen == 1)
            {
                MainWindow.Log($"[RtpCallRecorder][OUT] First outbound RTP packet: payloadType={payloadType}, payloadSize={payload.Length}, sourceRate={sourceRateHz}Hz");
            }
            else if (_outboundRtpPacketsSeen <= 10)
            {
                MainWindow.Log($"[RtpCallRecorder][OUT] Outbound RTP packet #{_outboundRtpPacketsSeen}: payloadType={payloadType}, payloadSize={payload.Length}");
            }
            else if (_outboundRtpPacketsSeen % 50 == 0)
            {
                MainWindow.Log($"[RtpCallRecorder][OUT] Outbound RTP packet #{_outboundRtpPacketsSeen}: payloadType={payloadType}, payloadSize={payload.Length}");
            }
            
            try
            {
                // Используем тот же метод декодирования, что и для inbound
                // DecodeRtpPayloadEx принимает payloadID (int), но мы передаем payloadType (byte)
                DecodedPcm decoded = DecodeRtpPayloadEx(payload, payloadType, SampleRate);
                if (decoded.Pcm48k == null || decoded.Pcm48k.Length == 0)
                {
                    if (_outboundRtpPacketsSeen <= 5)
                    {
                        MainWindow.Log($"[RtpCallRecorder][OUT] Decoded PCM is empty for packet #{_outboundRtpPacketsSeen}");
                    }
                    return;
                }
                
                // Записываем decoded PCM в outbound PCM файл (s16le, 48kHz, mono)
                lock (_lockObject)
                {
                    if (_pcmOutStream != null && _isRecording)
                    {
                        long positionBefore = _pcmOutStream.Position;
                        _pcmOutStream.Write(decoded.Pcm48k, 0, decoded.Pcm48k.Length);
                        _outboundRtpPayloadProcessed++;
                        
                        if (_outboundRtpPacketsSeen == 1)
                        {
                            MainWindow.Log($"[RtpCallRecorder][OUT] Written first outbound PCM: {decoded.Pcm48k.Length} bytes, position: {positionBefore} -> {_pcmOutStream.Position}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RtpCallRecorder][OUT] Error processing outbound RTP packet #{_outboundRtpPacketsSeen}: {ex.Message}, stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Обрабатывает исходящие raw samples напрямую из AudioSource (микрофон)
        /// Записывает в формате s16le, 48kHz, mono
        /// ПРИМЕЧАНИЕ: Этот метод больше не используется, так как WindowsAudioEndPoint не генерирует OnAudioSourceRawSample
        /// Теперь используется ProcessOutboundRtpPayload для записи из RTP send пакетов
        /// </summary>
        public void ProcessOutboundSamples(AudioSamplingRatesEnum rate, uint durationMs, short[] samples)
        {
            if (samples == null || samples.Length == 0)
            {
                if (_outboundPcmChunksSeen == 0)
                {
                    MainWindow.Log($"[RtpCallRecorder][OUT] ProcessOutboundSamples: WARNING - samples is null or empty");
                }
                return;
            }

            lock (_lockObject)
            {
                if (!_isRecording)
                {
                    if (_outboundPcmChunksSeen == 0)
                    {
                        MainWindow.Log($"[RtpCallRecorder][OUT] ProcessOutboundSamples: WARNING - not recording (isRecording={_isRecording})");
                    }
                    return;
                }

                if (_pcmOutStream == null)
                {
                    if (_outboundPcmChunksSeen == 0)
                    {
                        MainWindow.Log($"[RtpCallRecorder][OUT] ProcessOutboundSamples: ERROR - _pcmOutStream is null! Path={_pcmOutPath}");
                    }
                    return;
                }

                try
                {
                    // Конвертируем sample rate в int
                    int sampleRate = rate switch
                    {
                        AudioSamplingRatesEnum.Rate8KHz => 8000,
                        AudioSamplingRatesEnum.Rate16KHz => 16000,
                        _ => 48000
                    };

                    // Приводим к 48k mono s16le (если sampleRate != 48000)
                    short[] mono48 = samples;
                    if (sampleRate != SampleRate)
                    {
                        // Ресемплинг до 48kHz
                        byte[] pcmData = new byte[samples.Length * 2];
                        Buffer.BlockCopy(samples, 0, pcmData, 0, pcmData.Length);
                        byte[] resampled = ResamplePcm(pcmData, sampleRate, SampleRate);
                        
                        // Конвертируем обратно в short[]
                        mono48 = new short[resampled.Length / 2];
                        Buffer.BlockCopy(resampled, 0, mono48, 0, resampled.Length);
                    }

                    // short[] -> bytes (little-endian)
                    byte[] pcm = new byte[mono48.Length * 2];
                    Buffer.BlockCopy(mono48, 0, pcm, 0, pcm.Length);

                    long positionBefore = _pcmOutStream.Position;
                    _pcmOutStream.Write(pcm, 0, pcm.Length);
                    _outboundPcmChunksSeen++;

                    // Детальное логирование первого и первых 10 вызовов
                    bool isFirstCall = (_outboundPcmChunksSeen == 1);
                    if (isFirstCall || _outboundPcmChunksSeen <= 10)
                    {
                        int peak = mono48.Max(s => Math.Abs(s));
                        MainWindow.Log($"[RtpCallRecorder][OUT] chunk #{_outboundPcmChunksSeen}: rate={sampleRate}->48k, " +
                            $"inSamples={samples.Length}, outSamples={mono48.Length}, bytes={pcm.Length}, " +
                            $"peak={peak}, streamPosition={positionBefore}->{_pcmOutStream.Position}");
                    }
                    else if (_outboundPcmChunksSeen % 50 == 0)
                    {
                        MainWindow.Log($"[RtpCallRecorder][OUT] chunks={_outboundPcmChunksSeen}, bytes={_pcmOutStream.Position}");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[RtpCallRecorder] ERROR writing outbound PCM: {ex.Message}, stack: {ex.StackTrace}");
                }
            }
        }

        /// <summary>
        /// Обрабатывает исходящее PCM аудио напрямую из AudioSource (микрофон) - устаревший метод
        /// Записывает в формате s16le, 48kHz, mono
        /// </summary>
        [Obsolete("Use ProcessOutboundSamples instead")]
        public void ProcessOutboundPcm(byte[] pcmData, int sampleRate)
        {
            // ДЕТАЛЬНОЕ ЛОГИРОВАНИЕ для диагностики
            bool isFirstCall = (_outboundPcmChunksSeen == 0);
            
            if (pcmData == null || pcmData.Length == 0)
            {
                if (isFirstCall || _outboundPcmChunksSeen <= 3)
                {
                    MainWindow.Log($"[RtpCallRecorder][MIC] ProcessOutboundPcm: WARNING - pcmData is null or empty (chunks seen={_outboundPcmChunksSeen})");
                }
                return;
            }
            
            lock (_lockObject)
            {
                if (!_isRecording)
                {
                    if (isFirstCall || _outboundPcmChunksSeen <= 3)
                    {
                        MainWindow.Log($"[RtpCallRecorder][MIC] ProcessOutboundPcm: WARNING - not recording (isRecording={_isRecording}, chunks seen={_outboundPcmChunksSeen})");
                    }
                    return;
                }
                
                if (_pcmOutStream == null)
                {
                    if (isFirstCall || _outboundPcmChunksSeen <= 3)
                    {
                        MainWindow.Log($"[RtpCallRecorder][MIC] ProcessOutboundPcm: ERROR - _pcmOutStream is null! Path={_pcmOutPath}, chunks seen={_outboundPcmChunksSeen}");
                    }
                    return;
                }
                
                try
                {
                    // Конвертируем sample rate в 48kHz если нужно
                    // Входные данные уже в формате s16le (short[] -> byte[]), нужно только ресемплинг если sampleRate != 48kHz
                    byte[] pcm48k = pcmData;
                    if (sampleRate != SampleRate)
                    {
                        if (isFirstCall)
                        {
                            MainWindow.Log($"[RtpCallRecorder][MIC] Resampling from {sampleRate}Hz to {SampleRate}Hz");
                        }
                        pcm48k = ResamplePcm(pcmData, sampleRate, SampleRate);
                    }
                    
                    // Записываем в outbound PCM файл (s16le, 48kHz, mono)
                    long positionBefore = _pcmOutStream.Position;
                    _pcmOutStream.Write(pcm48k, 0, pcm48k.Length);
                    _outboundPcmChunksSeen++;
                    
                    // Детальное логирование первого и первых 10 вызовов
                    if (isFirstCall || _outboundPcmChunksSeen <= 10)
                    {
                        // Анализируем первые несколько сэмплов для диагностики
                        int samplesCount = pcm48k.Length / 2;
                        if (samplesCount > 0)
                        {
                            short[] firstSamples = new short[Math.Min(5, samplesCount)];
                            for (int i = 0; i < firstSamples.Length; i++)
                            {
                                firstSamples[i] = BitConverter.ToInt16(pcm48k, i * 2);
                            }
                            int peak = firstSamples.Max(s => Math.Abs(s));
                            
                            MainWindow.Log($"[RtpCallRecorder][MIC] ProcessOutboundPcm #{_outboundPcmChunksSeen}: " +
                                $"sampleRate={sampleRate}->48k, " +
                                $"dataLength={pcmData.Length}->{pcm48k.Length} bytes ({samplesCount} samples), " +
                                $"streamPosition={positionBefore}->{_pcmOutStream.Position}, " +
                                $"peak={peak}, first samples: [{string.Join(", ", firstSamples)}]");
                        }
                        else
                        {
                            MainWindow.Log($"[RtpCallRecorder][MIC] ProcessOutboundPcm #{_outboundPcmChunksSeen}: " +
                                $"sampleRate={sampleRate}->48k, " +
                                $"dataLength={pcmData.Length}->{pcm48k.Length} bytes, " +
                                $"streamPosition={positionBefore}->{_pcmOutStream.Position}");
                        }
                    }
                    else if (_outboundPcmChunksSeen % 100 == 0)
                    {
                        MainWindow.Log($"[RtpCallRecorder] ProcessOutboundPcm #{_outboundPcmChunksSeen}: still recording, streamPosition={_pcmOutStream.Position} bytes");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[RtpCallRecorder] ERROR writing outbound PCM: {ex.Message}, stack: {ex.StackTrace}");
                }
            }
        }
        
        /// <summary>
        /// Ресемплинг PCM аудио между разными частотами дискретизации
        /// </summary>
        private byte[] ResamplePcm(byte[] inputPcm, int inputRate, int outputRate)
        {
            if (inputRate == outputRate)
                return inputPcm;
            
            // Простой линейный ресемплинг (для 8kHz/16kHz -> 48kHz)
            int inputSamples = inputPcm.Length / 2; // 16-bit samples
            int outputSamples = (int)((long)inputSamples * outputRate / inputRate);
            byte[] outputPcm = new byte[outputSamples * 2];
            
            for (int i = 0; i < outputSamples; i++)
            {
                double ratio = (double)i * inputRate / outputRate;
                int inputIndex = (int)ratio;
                double fraction = ratio - inputIndex;
                
                if (inputIndex + 1 < inputSamples)
                {
                    // Линейная интерполяция между двумя соседними сэмплами
                    short sample1 = BitConverter.ToInt16(inputPcm, inputIndex * 2);
                    short sample2 = BitConverter.ToInt16(inputPcm, (inputIndex + 1) * 2);
                    short interpolated = (short)(sample1 + (sample2 - sample1) * fraction);
                    BitConverter.GetBytes(interpolated).CopyTo(outputPcm, i * 2);
                }
                else if (inputIndex < inputSamples)
                {
                    // Последний сэмпл
                    Array.Copy(inputPcm, inputIndex * 2, outputPcm, i * 2, 2);
                }
            }
            
            return outputPcm;
        }

        // Отслеживание залогированных payloadID (лог один раз для каждого payloadID)
        private readonly HashSet<int> _loggedPayloadIds = new HashSet<int>();
        private readonly object _decodeLogLock = new object();
        
        /// <summary>
        /// Результат декодирования RTP payload
        /// </summary>
        private sealed record DecodedPcm(byte[] Pcm48k, int SourceRate);
        
        /// <summary>
        /// Проверяет, нужно ли логировать для данного payloadID (true только первый раз для каждого ID)
        /// </summary>
        private bool ShouldLogPayload(int payloadId)
        {
            lock (_decodeLogLock)
            {
                return _loggedPayloadIds.Add(payloadId); // true только первый раз
            }
        }
        
        /// <summary>
        /// Декодирует RTP payload в PCM с информацией об исходной частоте дискретизации
        /// </summary>
        private DecodedPcm DecodeRtpPayloadEx(byte[] payload, int payloadID, int targetSampleRate)
        {
            try
            {
                if (payload == null || payload.Length == 0) 
                    return new DecodedPcm(Array.Empty<byte>(), 0);

                // Логируем выбор декодера один раз для каждого payloadID
                bool shouldLog = ShouldLogPayload(payloadID);

                byte[] result;
                string codecName;
                int sourceSampleRate;
                
                // Декодируем в зависимости от payload ID
                switch (payloadID)
                {
                    case 0: // PCMU (G.711 μ-law) - 8kHz
                        codecName = "PCMU (G.711 μ-law)";
                        sourceSampleRate = 8000;
                        result = DecodeG711MuLaw(payload, targetSampleRate, shouldLog);
                        break;
                    
                    case 8: // PCMA (G.711 A-law) - 8kHz
                        codecName = "PCMA (G.711 A-law)";
                        sourceSampleRate = 8000;
                        result = DecodeG711ALaw(payload, targetSampleRate, shouldLog);
                        break;
                    
                    case 9: // G.722 - 16kHz аудио (RTP timestamps могут быть 8kHz)
                        codecName = "G.722";
                        sourceSampleRate = 16000;
                        // G.722 пока не реализован - пропускаем вместо записи мусора
                        if (shouldLog)
                        {
                            MainWindow.Log($"[RtpCallRecorder] G.722 (payloadID=9) not implemented, SKIPPING audio (payloadSize={payload.Length})");
                        }
                        return new DecodedPcm(Array.Empty<byte>(), 0);
                    
                    case 111: // Opus - обычно 48kHz
                        codecName = "Opus";
                        sourceSampleRate = 48000;
                        result = DecodeOpus(payload, targetSampleRate);
                        break;
                    
                    case 13: // Comfort Noise (CN)
                        if (shouldLog)
                        {
                            MainWindow.Log($"[RtpCallRecorder] Comfort noise payload {payloadID} skipped (payloadSize={payload.Length})");
                        }
                        return new DecodedPcm(Array.Empty<byte>(), 0);
                    
                    case 96: // DTMF telephone-event (RFC 4733)
                    case 101: // DTMF telephone-event (RFC 4733)
                        if (shouldLog)
                        {
                            MainWindow.Log($"[RtpCallRecorder] DTMF/telephone-event payload {payloadID} skipped (payloadSize={payload.Length})");
                        }
                        return new DecodedPcm(Array.Empty<byte>(), 0);
                    
                    default:
                        // Для неизвестных кодеков НЕ записываем как PCM - это портит запись
                        // Вместо этого пропускаем и логируем
                        if (shouldLog)
                        {
                            MainWindow.Log($"[RtpCallRecorder] Unknown payload ID {payloadID}, SKIPPING audio (payloadSize={payload.Length})");
                        }
                        return new DecodedPcm(Array.Empty<byte>(), 0);
                }
                
                if (shouldLog)
                {
                    MainWindow.Log($"[RtpCallRecorder] Decoder selected: payloadID={payloadID} -> {codecName}, " +
                        $"sourceRate={sourceSampleRate}Hz, targetRate={targetSampleRate}Hz, " +
                        $"payloadSize={payload.Length} bytes, decodedSize={result.Length} bytes, " +
                        $"decodedSamples={result.Length / 2}");
                }
                
                return new DecodedPcm(result, sourceSampleRate);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RtpCallRecorder] Error decoding RTP payload (payloadID={payloadID}): {ex.Message}");
                return new DecodedPcm(Array.Empty<byte>(), 0);
            }
        }

        /// <summary>
        /// Декодирует G.711 μ-law в PCM
        /// </summary>
        private byte[] DecodeG711MuLaw(byte[] muLawData, int targetSampleRate, bool shouldLog = false)
        {
            int sourceSampleRate = 8000; // G.711 всегда 8kHz
            short[] pcmSamples = new short[muLawData.Length];
            
            // Декодируем μ-law в PCM используя NAudio.Codecs (надежный декодер)
            // ВАЖНО: передаем сырые байты из RTP payload БЕЗ инверсии (NAudio делает инверсию сам)
            // Некоторые реализации μ-law требуют инверсии битов (~byte), но NAudio.Codecs делает это внутри
            for (int i = 0; i < muLawData.Length; i++)
            {
                byte rawByte = muLawData[i];
                pcmSamples[i] = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(rawByte);
            }
            
            // Логируем уровни ДО ресемплинга
            if (shouldLog)
            {
                AnalyzeAndLogPcmLevelShort(pcmSamples, "BEFORE resample (8kHz)", showFirstSamples: true);
                
                // ДИАГНОСТИКА: проверяем декодирование с инверсией для сравнения (только для логов)
                short[] invertedSamples = new short[Math.Min(muLawData.Length, 10)];
                for (int i = 0; i < invertedSamples.Length; i++)
                {
                    byte invertedByte = (byte)~muLawData[i]; // Инверсия битов
                    invertedSamples[i] = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(invertedByte);
                }
                MainWindow.Log($"[RtpCallRecorder] MuLaw decode test: raw first10 samples peak={pcmSamples.Take(10).Max(s => Math.Abs(s))}, " +
                    $"inverted first10 samples peak={invertedSamples.Max(s => Math.Abs(s))}");
            }
            
            // Ресемплируем, если нужно
            if (targetSampleRate != sourceSampleRate)
            {
                short[] resampledSamples = ResampleShortArray(pcmSamples, sourceSampleRate, targetSampleRate, shouldLog);
                pcmSamples = resampledSamples;
                
                // Логируем уровни ПОСЛЕ ресемплинга
                if (shouldLog)
                {
                    AnalyzeAndLogPcmLevelShort(pcmSamples, "AFTER resample (48kHz)", showFirstSamples: true);
                }
            }
            
            // Конвертируем в byte[]
            byte[] result = new byte[pcmSamples.Length * 2];
            Buffer.BlockCopy(pcmSamples, 0, result, 0, result.Length);
            return result;
        }

        /// <summary>
        /// Декодирует G.711 A-law в PCM
        /// </summary>
        private byte[] DecodeG711ALaw(byte[] aLawData, int targetSampleRate, bool shouldLog = false)
        {
            int sourceSampleRate = 8000; // G.711 всегда 8kHz
            short[] pcmSamples = new short[aLawData.Length];
            
            // Декодируем A-law в PCM используя NAudio.Codecs (надежный декодер)
            for (int i = 0; i < aLawData.Length; i++)
            {
                pcmSamples[i] = NAudio.Codecs.ALawDecoder.ALawToLinearSample(aLawData[i]);
            }
            
            // Логируем уровни ДО ресемплинга
            if (shouldLog)
            {
                AnalyzeAndLogPcmLevelShort(pcmSamples, "BEFORE resample (8kHz)", showFirstSamples: true);
            }
            
            // Ресемплируем, если нужно
            if (targetSampleRate != sourceSampleRate)
            {
                short[] resampledSamples = ResampleShortArray(pcmSamples, sourceSampleRate, targetSampleRate, shouldLog);
                pcmSamples = resampledSamples;
                
                // Логируем уровни ПОСЛЕ ресемплинга
                if (shouldLog)
                {
                    AnalyzeAndLogPcmLevelShort(pcmSamples, "AFTER resample (48kHz)", showFirstSamples: true);
                }
            }
            
            // Конвертируем в byte[]
            byte[] result = new byte[pcmSamples.Length * 2];
            Buffer.BlockCopy(pcmSamples, 0, result, 0, result.Length);
            return result;
        }

        /// <summary>
        /// Декодирует Opus в PCM
        /// </summary>
        private byte[] DecodeOpus(byte[] opusData, int targetSampleRate)
        {
            try
            {
                if (opusData == null || opusData.Length == 0) return new byte[0];

                lock (_opusDecoderLock)
                {
                    // Получаем или создаем декодер для inbound потока
                    OpusDecoder? decoder = _opusDecoderInbound;
                    
                    if (decoder == null)
                    {
                        // Создаем новый декодер Opus (моно, 48kHz)
                        decoder = new OpusDecoder(targetSampleRate, 1); // 1 = моно канал
                        _opusDecoderInbound = decoder;
                        MainWindow.Log($"[RtpCallRecorder] Created Opus decoder for inbound stream, sample rate: {targetSampleRate}");
                    }

                    // Декодируем Opus данные
                    // Opus фреймы обычно 20ms, что при 48kHz = 960 сэмплов
                    int maxSamples = targetSampleRate / 50; // 20ms
                    short[] pcmSamples = new short[maxSamples];
                    
                    int decodedSamples = decoder.Decode(opusData, 0, opusData.Length, pcmSamples, 0, maxSamples, false);
                    
                    if (decodedSamples <= 0)
                    {
                        MainWindow.Log($"[RtpCallRecorder] Opus decode returned {decodedSamples} samples");
                        return new byte[0];
                    }

                    // Обрезаем до реального размера
                    short[] actualSamples = new short[decodedSamples];
                    Array.Copy(pcmSamples, 0, actualSamples, 0, decodedSamples);

                    // Конвертируем в byte[]
                    byte[] result = new byte[actualSamples.Length * 2];
                    Buffer.BlockCopy(actualSamples, 0, result, 0, result.Length);
                    
                    return result;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RtpCallRecorder] Error decoding Opus: {ex.Message}");
                return new byte[0];
            }
        }

        /// <summary>
        /// Ресемплирует short массив
        /// </summary>
        private short[] ResampleShortArray(short[] source, int sourceRate, int targetRate, bool shouldLog = false)
        {
            if (sourceRate == targetRate) return source;
            
            int sourceSamples = source.Length;
            int targetSamples = (int)(sourceSamples * (double)targetRate / sourceRate);
            short[] resampled = new short[targetSamples];
            
            if (shouldLog)
            {
                MainWindow.Log($"[RtpCallRecorder] Resampling: {sourceSamples} samples @ {sourceRate}Hz -> {targetSamples} samples @ {targetRate}Hz " +
                    $"(ratio={targetRate / (double)sourceRate:F3})");
            }
            
            for (int i = 0; i < targetSamples; i++)
            {
                double sourceIndex = i * (double)sourceRate / targetRate;
                int srcIdx = (int)sourceIndex;
                double fraction = sourceIndex - srcIdx;
                
                if (srcIdx < sourceSamples - 1)
                {
                    // Линейная интерполяция
                    resampled[i] = (short)(source[srcIdx] + (source[srcIdx + 1] - source[srcIdx]) * fraction);
                }
                else if (srcIdx < sourceSamples)
                {
                    resampled[i] = source[srcIdx];
                }
            }
            
            return resampled;
        }
        
        /// <summary>
        /// Анализирует уровни PCM из short массива и логирует
        /// </summary>
        private void AnalyzeAndLogPcmLevelShort(short[] pcmSamples, string label, bool showFirstSamples = false)
        {
            if (pcmSamples == null || pcmSamples.Length == 0) return;
            
            short peak = 0;
            long sumSquares = 0;
            
            for (int i = 0; i < pcmSamples.Length; i++)
            {
                short absSample = Math.Abs(pcmSamples[i]);
                if (absSample > peak)
                {
                    peak = absSample;
                }
                sumSquares += (long)pcmSamples[i] * pcmSamples[i];
            }
            
            int rms = pcmSamples.Length > 0 ? (int)Math.Sqrt(sumSquares / pcmSamples.Length) : 0;
            
            string firstSamplesStr = "";
            if (showFirstSamples && pcmSamples.Length > 0)
            {
                int samplesToShow = Math.Min(10, pcmSamples.Length);
                firstSamplesStr = ", first samples: [" + string.Join(", ", pcmSamples.Take(samplesToShow)) + "]";
            }
            
            MainWindow.Log($"[RtpCallRecorder] PCM level ({label}): peak={peak}, rms={rms}, samples={pcmSamples.Length}{firstSamplesStr}");
        }

        /// <summary>
        /// Останавливает запись и конвертирует PCM в WAV через ffmpeg
        /// </summary>
        public override async Task StopRecordingAsync(DateTime? callEndTime = null)
        {
            lock (_lockObject)
            {
                if (!_isRecording && _pcmInStream == null)
                {
                    return; // Ранний выход из async метода - это нормально
                }

                _isRecording = false;
            }

            // Закрываем PCM файлы
            try
            {
                lock (_lockObject)
                {
                    if (_pcmInStream != null)
                    {
                        _pcmInStream.Flush();
                        _pcmInStream.Dispose();
                        _pcmInStream = null;
                        MainWindow.Log($"[RtpCallRecorder] PCM inbound file closed: {_pcmInPath}");
                    }
                    
                    if (_pcmOutStream != null)
                    {
                        _pcmOutStream.Flush();
                        _pcmOutStream.Dispose();
                        _pcmOutStream = null;
                        MainWindow.Log($"[RtpCallRecorder] PCM outbound file closed: {_pcmOutPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RtpCallRecorder] Error closing PCM files: {ex.Message}");
            }

            // Микшируем оба PCM файла в один WAV через ffmpeg
            if (!string.IsNullOrEmpty(_pcmInPath) && !string.IsNullOrEmpty(_recordingFilePath))
            {
                long inboundSize = 0;
                long outboundSize = 0;
                bool hasInbound = File.Exists(_pcmInPath);
                bool hasOutbound = !string.IsNullOrEmpty(_pcmOutPath) && File.Exists(_pcmOutPath);
                
                if (hasInbound)
                {
                    inboundSize = new FileInfo(_pcmInPath).Length;
                    MainWindow.Log($"[RtpCallRecorder] Inbound PCM file: {Path.GetFileName(_pcmInPath)}, size={inboundSize} bytes ({inboundSize / 1024} KB)");
                }
                else
                {
                    MainWindow.Log($"[RtpCallRecorder] WARNING: Inbound PCM file not found: {_pcmInPath}");
                }
                
                if (hasOutbound && !string.IsNullOrEmpty(_pcmOutPath))
                {
                    outboundSize = new FileInfo(_pcmOutPath).Length;
                    MainWindow.Log($"[RtpCallRecorder] Outbound PCM file: {Path.GetFileName(_pcmOutPath)}, size={outboundSize} bytes ({outboundSize / 1024} KB), chunks recorded={_outboundPcmChunksSeen}");
                }
                else
                {
                    MainWindow.Log($"[RtpCallRecorder] WARNING: Outbound PCM file not found: {_pcmOutPath}, chunks recorded={_outboundPcmChunksSeen}");
                }
                
                // ИТОГОВАЯ СВОДКА для диагностики
                MainWindow.Log($"[RtpCallRecorder] === RECORDING SUMMARY ===");
                MainWindow.Log($"[RtpCallRecorder] Inbound: {inboundSize} bytes ({inboundSize / 1024} KB), RTP packets: {_inboundRtpPacketsSeen}");
                MainWindow.Log($"[RtpCallRecorder] Outbound: {outboundSize} bytes ({outboundSize / 1024} KB), RTP packets: {_outboundRtpPacketsSeen}, PCM chunks: {_outboundPcmChunksSeen}, processed: {_outboundRtpPayloadProcessed}");
                MainWindow.Log($"[RtpCallRecorder] =========================");
                
                hasInbound = hasInbound && inboundSize > 0;
                hasOutbound = hasOutbound && outboundSize > 0;
                
                if (!hasInbound && !hasOutbound)
                {
                    MainWindow.Log($"[RtpCallRecorder] WARNING: Both PCM files are empty or missing, skipping conversion");
                    _recordingFilePath = null;
                    return;
                }
                
                // Если есть оба файла - микшируем, иначе конвертируем только один
                bool converted = false;
                if (hasInbound && hasOutbound && !string.IsNullOrEmpty(_pcmOutPath))
                {
                    MainWindow.Log($"[RtpCallRecorder] Mixing inbound and outbound PCM files into WAV...");
                    converted = await MixPcmFilesWithFfmpegAsync(_pcmInPath, _pcmOutPath, _recordingFilePath);

                    // Если стерео/моно‑микс по какой-то причине не удался – пробуем хотя бы inbound‑канал.
                    if (!converted && hasInbound)
                    {
                        MainWindow.Log($"[RtpCallRecorder] WARNING: mixing failed, falling back to inbound‑only WAV...");
                        converted = await ConvertPcmToWavWithFfmpegAsync(_pcmInPath, _recordingFilePath);
                    }
                }
                else if (hasInbound)
                {
                    MainWindow.Log($"[RtpCallRecorder] Converting inbound PCM to WAV (outbound file missing or empty)...");
                    converted = await ConvertPcmToWavWithFfmpegAsync(_pcmInPath, _recordingFilePath);
                }
                else if (hasOutbound && !string.IsNullOrEmpty(_pcmOutPath))
                {
                    MainWindow.Log($"[RtpCallRecorder] Converting outbound PCM to WAV (inbound file missing or empty)...");
                    converted = await ConvertPcmToWavWithFfmpegAsync(_pcmOutPath, _recordingFilePath);
                }
                
                if (converted)
                {
                    MainWindow.Log($"[RtpCallRecorder] Final WAV ready: {_recordingFilePath}");
                    
                    // Удаляем PCM файлы после успешной конвертации
                    try
                    {
                        if (hasInbound) File.Delete(_pcmInPath);
                        if (hasOutbound && !string.IsNullOrEmpty(_pcmOutPath)) File.Delete(_pcmOutPath);
                        MainWindow.Log($"[RtpCallRecorder] PCM files deleted");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[RtpCallRecorder] Warning: Could not delete PCM files: {ex.Message}");
                    }
                }
                else
                {
                    MainWindow.Log($"[RtpCallRecorder] WARNING: ffmpeg conversion failed, PCM files kept");
                    if (!string.IsNullOrEmpty(_recordingFilePath) && !File.Exists(_recordingFilePath))
                    {
                        _recordingFilePath = null;
                    }
                }
            }
            else
            {
                MainWindow.Log($"[RtpCallRecorder] WARNING: Cannot convert - PCM paths or WAV path not set");
                _recordingFilePath = null;
            }

            // Освобождаем Opus декодер
            lock (_opusDecoderLock)
            {
                _opusDecoderInbound = null;
            }
        }

        /// <summary>
        /// Микширует два PCM файла (inbound и outbound) в один WAV файл через ffmpeg.
        /// МОНО режим: оба голоса суммируются в один канал (центр).
        /// </summary>
        private async Task<bool> MixPcmFilesWithFfmpegAsync(string inboundPcmPath, string outboundPcmPath, string wavPath)
        {
            if (!File.Exists(inboundPcmPath) || !File.Exists(outboundPcmPath))
            {
                MainWindow.Log($"[RtpCallRecorder] PCM files not found for mixing");
                return false;
            }

            string? ffmpegPath = FfmpegHelper.FindFfmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                MainWindow.Log($"[RtpCallRecorder] ffmpeg not found, skipping mixing. PCM files will be kept.");
                return false;
            }

            MainWindow.Log($"[RtpCallRecorder] Starting ffmpeg mixing (mono): {Path.GetFileName(inboundPcmPath)} + {Path.GetFileName(outboundPcmPath)} -> {Path.GetFileName(wavPath)}");

            // МОНО режим with audio processing:
            //  1. afftdn on mic — FFT-based noise reduction (removes hiss/hum from WASAPI AEC residual)
            //  2. volume=2.5 on mic — compensate for WASAPI Communications mode attenuation
            //  3. amerge + pan=mono — sum both streams at full level
            //  4. loudnorm — EBU R128 loudness normalisation (both voices at same perceived level)
            string args = $"-y -hide_banner -loglevel error " +
                $"-f s16le -ar 48000 -ac 1 -i \"{outboundPcmPath}\" " +
                $"-f s16le -ar 48000 -ac 1 -i \"{inboundPcmPath}\" " +
                $"-filter_complex \"" +
                $"[0:a]afftdn=nr=12:nf=-25,volume=2.5[mic];" +
                $"[mic][1:a]amerge=inputs=2,pan=mono|c0=c0+c1," +
                $"loudnorm=I=-16:TP=-1.5:LRA=11\" " +
                $"-ac 1 -acodec pcm_s16le -ar 48000 \"{wavPath}\"";

            // Используем размер большего из двух PCM файлов для расчета таймаута
            long inputSize = 0;
            if (File.Exists(outboundPcmPath)) inputSize = Math.Max(inputSize, new FileInfo(outboundPcmPath).Length);
            if (File.Exists(inboundPcmPath)) inputSize = Math.Max(inputSize, new FileInfo(inboundPcmPath).Length);
            string? inputFileForTimeout = inputSize > 0 && File.Exists(outboundPcmPath) ? outboundPcmPath : null;
            
            return await FfmpegHelper.ConvertAsync(ffmpegPath, args, wavPath, "[RtpCallRecorder]", inputFileForTimeout);
        }

        /// <summary>
        /// Конвертирует RAW PCM файл в WAV используя ffmpeg
        /// Формат входного PCM: s16le, 48kHz, mono
        /// </summary>
        private async Task<bool> ConvertPcmToWavWithFfmpegAsync(string pcmPath, string wavPath)
        {
            if (!File.Exists(pcmPath))
            {
                MainWindow.Log($"[RtpCallRecorder] PCM file not found: {pcmPath}");
                return false;
            }

            string? ffmpegPath = FfmpegHelper.FindFfmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                MainWindow.Log($"[RtpCallRecorder] ffmpeg not found, skipping conversion. PCM file will be kept: {pcmPath}");
                return false;
            }

            MainWindow.Log($"[RtpCallRecorder] Starting ffmpeg conversion: {Path.GetFileName(pcmPath)} -> {Path.GetFileName(wavPath)}");

            // s16le, 48kHz, mono → WAV (PCM, 16-bit, 48kHz, mono)
            string args = $"-y -hide_banner -loglevel error " +
                $"-f s16le -ar 48000 -ac 1 -i \"{pcmPath}\" " +
                $"-acodec pcm_s16le -ar 48000 -ac 1 \"{wavPath}\"";

            return await FfmpegHelper.ConvertAsync(ffmpegPath, args, wavPath, "[RtpCallRecorder]", pcmPath);
        }

        /// <summary>
        /// Recovers WAV when StopCallRecording was skipped but orphan *_inbound.pcm / *_outbound.pcm remain.
        /// </summary>
        public static async Task<bool> TryRecoverWavFromOrphanPcmAsync(string wavPath)
        {
            if (string.IsNullOrWhiteSpace(wavPath))
                return false;

            try
            {
                if (File.Exists(wavPath) && new FileInfo(wavPath).Length > 0)
                    return true;

                string? dir = Path.GetDirectoryName(wavPath);
                string stem = Path.GetFileNameWithoutExtension(wavPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(stem) || !Directory.Exists(dir))
                    return false;

                string inPcm = Path.Combine(dir, stem + "_inbound.pcm");
                string outPcm = Path.Combine(dir, stem + "_outbound.pcm");
                bool hasIn = File.Exists(inPcm) && new FileInfo(inPcm).Length > 0;
                bool hasOut = File.Exists(outPcm) && new FileInfo(outPcm).Length > 0;
                if (!hasIn && !hasOut)
                    return false;

                MainWindow.Log($"[RtpCallRecorder] Recovering orphan PCM → WAV: {Path.GetFileName(wavPath)} (in={hasIn}, out={hasOut})");

                var helper = new RtpCallRecorder();
                bool converted = false;
                if (hasIn && hasOut)
                {
                    converted = await helper.MixPcmFilesWithFfmpegAsync(inPcm, outPcm, wavPath).ConfigureAwait(false);
                    if (!converted && hasIn)
                        converted = await helper.ConvertPcmToWavWithFfmpegAsync(inPcm, wavPath).ConfigureAwait(false);
                }
                else if (hasIn)
                    converted = await helper.ConvertPcmToWavWithFfmpegAsync(inPcm, wavPath).ConfigureAwait(false);
                else
                    converted = await helper.ConvertPcmToWavWithFfmpegAsync(outPcm, wavPath).ConfigureAwait(false);

                if (converted && File.Exists(wavPath))
                {
                    MainWindow.Log($"[RtpCallRecorder] Orphan PCM recovery OK: {wavPath}");
                    try
                    {
                        if (hasIn) File.Delete(inPcm);
                        if (hasOut) File.Delete(outPcm);
                    }
                    catch { /* ignore */ }
                    return true;
                }

                MainWindow.Log($"[RtpCallRecorder] Orphan PCM recovery failed for {Path.GetFileName(wavPath)}");
                return false;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RtpCallRecorder] Orphan PCM recovery error: {ex.Message}");
                return false;
            }
        }

        public static bool TryRecoverWavFromOrphanPcm(string wavPath, int timeoutMs = 45000)
        {
            try
            {
                return TryRecoverWavFromOrphanPcmAsync(wavPath)
                    .Wait(timeoutMs);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RtpCallRecorder] Orphan PCM recovery sync wait failed: {ex.Message}");
                return false;
            }
        }
    }
}
