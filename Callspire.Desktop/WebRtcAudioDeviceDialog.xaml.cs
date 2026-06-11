#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Softphone
{
    public partial class WebRtcAudioDeviceDialog : Window
    {
        public string? SelectedInputDeviceId { get; private set; }
        public string? SelectedOutputDeviceId { get; private set; }
        
        private List<WebRtcAudioDevice> _inputs;
        private List<WebRtcAudioDevice> _outputs;

        public WebRtcAudioDeviceDialog(List<WebRtcAudioDevice> inputs, List<WebRtcAudioDevice> outputs)
        {
            InitializeComponent();
            NativeWindowAppearanceManager.Attach(this);
            _inputs = inputs;
            _outputs = outputs;
            
            // Заполняем комбобоксы
            MicrophoneComboBox.ItemsSource = inputs;
            SpeakerComboBox.ItemsSource = outputs;
            
            // Выбираем первые устройства по умолчанию
            if (inputs.Count > 0)
            {
                MicrophoneComboBox.SelectedIndex = 0;
            }
            if (outputs.Count > 0)
            {
                SpeakerComboBox.SelectedIndex = 0;
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            if (MicrophoneComboBox.SelectedItem is WebRtcAudioDevice micDevice)
            {
                SelectedInputDeviceId = micDevice.DeviceId;
            }
            
            if (SpeakerComboBox.SelectedItem is WebRtcAudioDevice speakerDevice)
            {
                SelectedOutputDeviceId = speakerDevice.DeviceId;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}


#endif // WINDOWS
