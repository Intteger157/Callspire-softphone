#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;

namespace Softphone
{
    public class AudioDeviceInfo
    {
        public int DeviceNumber { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Guid { get; set; } = string.Empty;

        public override string ToString()
        {
            return Name;
        }
    }

    public static class AudioDeviceHelper
    {
        public static List<AudioDeviceInfo> GetMicrophones()
        {
            var devices = new List<AudioDeviceInfo>();
            
            try
            {
                for (int i = 0; i < WaveIn.DeviceCount; i++)
                {
                    var capabilities = WaveIn.GetCapabilities(i);
                    devices.Add(new AudioDeviceInfo
                    {
                        DeviceNumber = i,
                        Name = capabilities.ProductName,
                        Guid = capabilities.NameGuid.ToString()
                    });
                }
            }
            catch (Exception)
            {
                // Если не удалось получить устройства, возвращаем пустой список
            }

            return devices;
        }

        public static List<AudioDeviceInfo> GetSpeakers()
        {
            var devices = new List<AudioDeviceInfo>();
            
            try
            {
                for (int i = 0; i < WaveOut.DeviceCount; i++)
                {
                    var capabilities = WaveOut.GetCapabilities(i);
                    devices.Add(new AudioDeviceInfo
                    {
                        DeviceNumber = i,
                        Name = capabilities.ProductName,
                        Guid = capabilities.NameGuid.ToString()
                    });
                }
            }
            catch (Exception)
            {
                // Если не удалось получить устройства, возвращаем пустой список
            }

            return devices;
        }

        public static AudioDeviceInfo? GetDefaultMicrophone()
        {
            var microphones = GetMicrophones();
            return microphones.FirstOrDefault();
        }

        public static AudioDeviceInfo? GetDefaultSpeaker()
        {
            var speakers = GetSpeakers();
            return speakers.FirstOrDefault();
        }
    }
}


#endif // WINDOWS
