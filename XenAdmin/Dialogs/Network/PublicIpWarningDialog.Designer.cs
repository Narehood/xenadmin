namespace XenAdmin.Dialogs.Network
{
    partial class PublicIpWarningDialog
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.pictureBoxIcon = new System.Windows.Forms.PictureBox();
            this.labelMessage = new System.Windows.Forms.Label();
            this.checkBoxDoNotAsk = new System.Windows.Forms.CheckBox();
            this.buttonProceed = new System.Windows.Forms.Button();
            this.buttonCancel = new System.Windows.Forms.Button();
            this.tableLayoutPanel1 = new System.Windows.Forms.TableLayoutPanel();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBoxIcon)).BeginInit();
            this.tableLayoutPanel1.SuspendLayout();
            this.SuspendLayout();
            //
            // pictureBoxIcon
            //
            this.pictureBoxIcon.Image = global::XenAdmin.Properties.Resources._000_WarningAlert_h32bit_32;
            this.pictureBoxIcon.Location = new System.Drawing.Point(3, 3);
            this.pictureBoxIcon.Name = "pictureBoxIcon";
            this.pictureBoxIcon.Size = new System.Drawing.Size(32, 32);
            this.pictureBoxIcon.SizeMode = System.Windows.Forms.PictureBoxSizeMode.AutoSize;
            this.pictureBoxIcon.TabIndex = 0;
            this.pictureBoxIcon.TabStop = false;
            //
            // labelMessage
            //
            this.labelMessage.AutoSize = true;
            this.labelMessage.Dock = System.Windows.Forms.DockStyle.Fill;
            this.labelMessage.Location = new System.Drawing.Point(51, 0);
            this.labelMessage.Margin = new System.Windows.Forms.Padding(10, 0, 3, 0);
            this.labelMessage.MaximumSize = new System.Drawing.Size(420, 0);
            this.labelMessage.Name = "labelMessage";
            this.labelMessage.Size = new System.Drawing.Size(420, 80);
            this.labelMessage.TabIndex = 1;
            this.labelMessage.Text = "warning";
            //
            // checkBoxDoNotAsk
            //
            this.checkBoxDoNotAsk.AutoSize = true;
            this.tableLayoutPanel1.SetColumnSpan(this.checkBoxDoNotAsk, 2);
            this.checkBoxDoNotAsk.Location = new System.Drawing.Point(3, 95);
            this.checkBoxDoNotAsk.Name = "checkBoxDoNotAsk";
            this.checkBoxDoNotAsk.Size = new System.Drawing.Size(200, 17);
            this.checkBoxDoNotAsk.TabIndex = 2;
            this.checkBoxDoNotAsk.UseVisualStyleBackColor = true;
            //
            // buttonProceed
            //
            this.buttonProceed.Anchor = System.Windows.Forms.AnchorStyles.Right;
            this.buttonProceed.DialogResult = System.Windows.Forms.DialogResult.OK;
            this.buttonProceed.Location = new System.Drawing.Point(291, 130);
            this.buttonProceed.Name = "buttonProceed";
            this.buttonProceed.Size = new System.Drawing.Size(90, 27);
            this.buttonProceed.TabIndex = 3;
            this.buttonProceed.UseVisualStyleBackColor = true;
            //
            // buttonCancel
            //
            this.buttonCancel.Anchor = System.Windows.Forms.AnchorStyles.Left;
            this.buttonCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.buttonCancel.Location = new System.Drawing.Point(387, 130);
            this.buttonCancel.Name = "buttonCancel";
            this.buttonCancel.Size = new System.Drawing.Size(90, 27);
            this.buttonCancel.TabIndex = 4;
            this.buttonCancel.UseVisualStyleBackColor = true;
            //
            // tableLayoutPanel1
            //
            this.tableLayoutPanel1.AutoSize = true;
            this.tableLayoutPanel1.ColumnCount = 3;
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tableLayoutPanel1.Controls.Add(this.pictureBoxIcon, 0, 0);
            this.tableLayoutPanel1.Controls.Add(this.labelMessage, 1, 0);
            this.tableLayoutPanel1.Controls.Add(this.checkBoxDoNotAsk, 0, 1);
            this.tableLayoutPanel1.Controls.Add(this.buttonProceed, 1, 2);
            this.tableLayoutPanel1.Controls.Add(this.buttonCancel, 2, 2);
            this.tableLayoutPanel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tableLayoutPanel1.Location = new System.Drawing.Point(12, 12);
            this.tableLayoutPanel1.Name = "tableLayoutPanel1";
            this.tableLayoutPanel1.Padding = new System.Windows.Forms.Padding(0, 0, 0, 4);
            this.tableLayoutPanel1.RowCount = 3;
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tableLayoutPanel1.Size = new System.Drawing.Size(480, 165);
            this.tableLayoutPanel1.TabIndex = 0;
            this.tableLayoutPanel1.SetColumnSpan(this.labelMessage, 2);
            //
            // PublicIpWarningDialog
            //
            this.AcceptButton = this.buttonProceed;
            this.AutoScaleDimensions = new System.Drawing.SizeF(96F, 96F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.AutoSize = true;
            this.CancelButton = this.buttonCancel;
            this.ClientSize = new System.Drawing.Size(504, 189);
            this.Controls.Add(this.tableLayoutPanel1);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "PublicIpWarningDialog";
            this.Padding = new System.Windows.Forms.Padding(12);
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            ((System.ComponentModel.ISupportInitialize)(this.pictureBoxIcon)).EndInit();
            this.tableLayoutPanel1.ResumeLayout(false);
            this.tableLayoutPanel1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private System.Windows.Forms.PictureBox pictureBoxIcon;
        private System.Windows.Forms.Label labelMessage;
        private System.Windows.Forms.CheckBox checkBoxDoNotAsk;
        private System.Windows.Forms.Button buttonProceed;
        private System.Windows.Forms.Button buttonCancel;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel1;
    }
}
