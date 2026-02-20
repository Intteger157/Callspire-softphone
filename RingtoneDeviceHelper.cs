using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace Softphone
{
    public class RingtoneOutputDeviceInfo
    {
        public string Id { get; set; } = ""; // empty => default
        public string Name { get; set; } = "";

        public override string ToString() => Name;
    }

    public static class RingtoneDeviceHelper
    {
        public static List<RingtoneOutputDeviceInfo> GetOutputDevices()
        {
            var result = new List<RingtoneOutputDeviceInfo>();

            // Default entry
            result.Add(new RingtoneOutputDeviceInfo
            {
                Id = "",
                Name = "Default output device"
            });

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                foreach (var d in devices)
                {
                    result.Add(new RingtoneOutputDeviceInfo
                    {
                        Id = d.ID,
                        Name = d.FriendlyName
                    });
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RingtoneDeviceHelper] Error enumerating output devices: {ex.Message}");
            }

            return result;
        }
    }
}


