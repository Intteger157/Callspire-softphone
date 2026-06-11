using System;
using System.IO;

namespace Softphone.Audio
{
    /// <summary>
    /// Minimal PCM16 WAV writer (replaces NAudio's WaveFileWriter for diagnostic dumps
    /// so Callspire.Core stays NAudio-free). Header sizes are fixed up on Dispose.
    /// </summary>
    public sealed class WavFileWriter : IDisposable
    {
        private readonly FileStream _stream;
        private readonly int _sampleRate;
        private readonly short _channels;
        private long _dataBytes;
        private bool _disposed;

        public WavFileWriter(string path, int sampleRate, int channels)
        {
            _sampleRate = sampleRate;
            _channels = (short)channels;
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteHeader(placeholder: true);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (_disposed) return;
            _stream.Write(buffer, offset, count);
            _dataBytes += count;
        }

        private void WriteHeader(bool placeholder)
        {
            var w = new BinaryWriter(_stream);
            int byteRate = _sampleRate * _channels * 2;
            short blockAlign = (short)(_channels * 2);
            uint dataLen = placeholder ? 0u : (uint)Math.Min(_dataBytes, uint.MaxValue);

            _stream.Seek(0, SeekOrigin.Begin);
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataLen);
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);                 // PCM fmt chunk size
            w.Write((short)1);           // PCM
            w.Write(_channels);
            w.Write(_sampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write((short)16);          // bits per sample
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataLen);
            w.Flush();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                WriteHeader(placeholder: false);
            }
            catch
            {
                // best-effort diagnostic file
            }
            finally
            {
                _stream.Dispose();
            }
        }
    }
}
