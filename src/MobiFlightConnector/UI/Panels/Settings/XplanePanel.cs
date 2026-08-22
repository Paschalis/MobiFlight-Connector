using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using MobiFlight.xplane;

namespace MobiFlight.UI.Panels.Settings
{
    public partial class XplanePanel : UserControl
    {
        private CancellationTokenSource _testCancellation;

        public XplanePanel()
        {
            InitializeComponent();
        }

        public void LoadSettings()
        {
            xplaneRemoteEnabledCheckBox.Checked = Properties.Settings.Default.XplaneRemoteEnabled;
            xplaneHostTextBox.Text = Properties.Settings.Default.XplaneHost;
            xplanePortTextBox.Text = Properties.Settings.Default.XplanePort.ToString();

            UpdateControlState();
        }

        public void SaveSettings()
        {
            Properties.Settings.Default.XplaneRemoteEnabled = xplaneRemoteEnabledCheckBox.Checked;
            Properties.Settings.Default.XplaneHost = xplaneHostTextBox.Text.Trim();

            var port = Properties.Settings.Default.XplanePort;
            if (Int32.TryParse(xplanePortTextBox.Text, out var result) && result > 0 && result <= 65535)
            {
                port = result;
            }
            Properties.Settings.Default.XplanePort = port;
        }

        /// <summary>
        /// Reads the endpoint straight from the controls so the test button works on the values the
        /// user just typed, without having to save first.
        /// </summary>
        private XplaneConnectionSettings CurrentSettings()
        {
            var port = XplaneConnectionSettings.DefaultPort;
            if (Int32.TryParse(xplanePortTextBox.Text, out var parsed))
            {
                port = parsed;
            }

            return new XplaneConnectionSettings()
            {
                RemoteEnabled = xplaneRemoteEnabledCheckBox.Checked,
                Host = xplaneHostTextBox.Text.Trim(),
                Port = port
            };
        }

        private void UpdateControlState()
        {
            var remote = xplaneRemoteEnabledCheckBox.Checked;
            xplaneHostTextBox.Enabled = remote;
            xplanePortTextBox.Enabled = remote;
            hostLabel.Enabled = remote;
            portLabel.Enabled = remote;
        }

        private void xplaneRemoteEnabledCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            UpdateControlState();
        }

        private async void xplaneTestButton_Click(object sender, EventArgs e)
        {
            if (_testCancellation != null) return;

            _testCancellation = new CancellationTokenSource();
            xplaneTestButton.Enabled = false;
            xplaneTestResultLabel.ForeColor = SystemColors.ControlText;
            xplaneTestResultLabel.Text = "Testing...";

            try
            {
                var result = await XplaneConnectionTester.TestAsync(
                    CurrentSettings(),
                    TimeSpan.FromSeconds(5),
                    _testCancellation.Token);

                xplaneTestResultLabel.ForeColor = result.IsSuccess ? Color.Green : Color.Firebrick;
                xplaneTestResultLabel.Text = result.IsSuccess
                    ? $"Connected. X-Plane replied in {result.RoundTrip.TotalMilliseconds:F0} ms."
                    : result.Message;
            }
            catch (Exception ex)
            {
                xplaneTestResultLabel.ForeColor = Color.Firebrick;
                xplaneTestResultLabel.Text = $"Test failed: {ex.Message}";
            }
            finally
            {
                _testCancellation.Dispose();
                _testCancellation = null;
                xplaneTestButton.Enabled = true;
            }
        }
    }
}
