namespace MobiFlight.UI.Panels.Settings
{
    partial class XplanePanel
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.xplaneRemoteEnabledCheckBox = new System.Windows.Forms.CheckBox();
            this.hostLabel = new System.Windows.Forms.Label();
            this.xplaneHostTextBox = new System.Windows.Forms.TextBox();
            this.portLabel = new System.Windows.Forms.Label();
            this.xplanePortTextBox = new System.Windows.Forms.TextBox();
            this.xplaneTestButton = new System.Windows.Forms.Button();
            this.xplaneTestResultLabel = new System.Windows.Forms.Label();
            this.hintLabel = new System.Windows.Forms.Label();
            this.groupBox1.SuspendLayout();
            this.SuspendLayout();
            //
            // groupBox1
            //
            this.groupBox1.Controls.Add(this.hintLabel);
            this.groupBox1.Controls.Add(this.xplaneTestResultLabel);
            this.groupBox1.Controls.Add(this.xplaneTestButton);
            this.groupBox1.Controls.Add(this.xplanePortTextBox);
            this.groupBox1.Controls.Add(this.portLabel);
            this.groupBox1.Controls.Add(this.xplaneHostTextBox);
            this.groupBox1.Controls.Add(this.hostLabel);
            this.groupBox1.Controls.Add(this.xplaneRemoteEnabledCheckBox);
            this.groupBox1.Location = new System.Drawing.Point(4, 4);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Size = new System.Drawing.Size(551, 190);
            this.groupBox1.TabIndex = 0;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "X-Plane Settings";
            //
            // xplaneRemoteEnabledCheckBox
            //
            this.xplaneRemoteEnabledCheckBox.AutoSize = true;
            this.xplaneRemoteEnabledCheckBox.Location = new System.Drawing.Point(9, 24);
            this.xplaneRemoteEnabledCheckBox.Name = "xplaneRemoteEnabledCheckBox";
            this.xplaneRemoteEnabledCheckBox.Size = new System.Drawing.Size(247, 17);
            this.xplaneRemoteEnabledCheckBox.TabIndex = 0;
            this.xplaneRemoteEnabledCheckBox.Text = "X-Plane runs on a different computer";
            this.xplaneRemoteEnabledCheckBox.UseVisualStyleBackColor = true;
            this.xplaneRemoteEnabledCheckBox.CheckedChanged += new System.EventHandler(this.xplaneRemoteEnabledCheckBox_CheckedChanged);
            //
            // hostLabel
            //
            this.hostLabel.AutoSize = true;
            this.hostLabel.Location = new System.Drawing.Point(6, 56);
            this.hostLabel.Name = "hostLabel";
            this.hostLabel.Size = new System.Drawing.Size(32, 13);
            this.hostLabel.TabIndex = 1;
            this.hostLabel.Text = "Host:";
            //
            // xplaneHostTextBox
            //
            this.xplaneHostTextBox.Location = new System.Drawing.Point(49, 53);
            this.xplaneHostTextBox.Name = "xplaneHostTextBox";
            this.xplaneHostTextBox.Size = new System.Drawing.Size(154, 20);
            this.xplaneHostTextBox.TabIndex = 2;
            //
            // portLabel
            //
            this.portLabel.AutoSize = true;
            this.portLabel.Location = new System.Drawing.Point(6, 82);
            this.portLabel.Name = "portLabel";
            this.portLabel.Size = new System.Drawing.Size(29, 13);
            this.portLabel.TabIndex = 3;
            this.portLabel.Text = "Port:";
            //
            // xplanePortTextBox
            //
            this.xplanePortTextBox.Location = new System.Drawing.Point(49, 79);
            this.xplanePortTextBox.Name = "xplanePortTextBox";
            this.xplanePortTextBox.Size = new System.Drawing.Size(100, 20);
            this.xplanePortTextBox.TabIndex = 4;
            //
            // xplaneTestButton
            //
            this.xplaneTestButton.Location = new System.Drawing.Point(9, 111);
            this.xplaneTestButton.Name = "xplaneTestButton";
            this.xplaneTestButton.Size = new System.Drawing.Size(110, 23);
            this.xplaneTestButton.TabIndex = 5;
            this.xplaneTestButton.Text = "Test connection";
            this.xplaneTestButton.UseVisualStyleBackColor = true;
            this.xplaneTestButton.Click += new System.EventHandler(this.xplaneTestButton_Click);
            //
            // xplaneTestResultLabel
            //
            this.xplaneTestResultLabel.Location = new System.Drawing.Point(125, 111);
            this.xplaneTestResultLabel.Name = "xplaneTestResultLabel";
            this.xplaneTestResultLabel.Size = new System.Drawing.Size(415, 40);
            this.xplaneTestResultLabel.TabIndex = 6;
            this.xplaneTestResultLabel.Text = "";
            //
            // hintLabel
            //
            this.hintLabel.Location = new System.Drawing.Point(9, 155);
            this.hintLabel.Name = "hintLabel";
            this.hintLabel.Size = new System.Drawing.Size(531, 30);
            this.hintLabel.TabIndex = 7;
            this.hintLabel.Text = "On the machine running X-Plane, enable Settings > Network > \"Accept incoming conne" +
                "ctions\" and allow UDP port 49000 through its firewall.";
            //
            // XplanePanel
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.groupBox1);
            this.Name = "XplanePanel";
            this.Size = new System.Drawing.Size(558, 524);
            this.groupBox1.ResumeLayout(false);
            this.groupBox1.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.CheckBox xplaneRemoteEnabledCheckBox;
        private System.Windows.Forms.Label hostLabel;
        private System.Windows.Forms.TextBox xplaneHostTextBox;
        private System.Windows.Forms.Label portLabel;
        private System.Windows.Forms.TextBox xplanePortTextBox;
        private System.Windows.Forms.Button xplaneTestButton;
        private System.Windows.Forms.Label xplaneTestResultLabel;
        private System.Windows.Forms.Label hintLabel;
    }
}
