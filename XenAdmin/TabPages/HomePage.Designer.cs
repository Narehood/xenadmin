namespace XenAdmin.TabPages
{
    partial class HomePage
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
            this.layoutPanel = new System.Windows.Forms.TableLayoutPanel();
            this.labelTitle = new System.Windows.Forms.Label();
            this.labelBlurb = new System.Windows.Forms.Label();
            this.buttonAddServer = new System.Windows.Forms.Button();
            this.layoutPanel.SuspendLayout();
            this.SuspendLayout();
            //
            // layoutPanel
            //
            this.layoutPanel.ColumnCount = 1;
            this.layoutPanel.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutPanel.Controls.Add(this.labelTitle, 0, 0);
            this.layoutPanel.Controls.Add(this.labelBlurb, 0, 1);
            this.layoutPanel.Controls.Add(this.buttonAddServer, 0, 2);
            this.layoutPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.layoutPanel.Location = new System.Drawing.Point(0, 0);
            this.layoutPanel.Name = "layoutPanel";
            this.layoutPanel.Padding = new System.Windows.Forms.Padding(40);
            this.layoutPanel.RowCount = 4;
            this.layoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.layoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.layoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.layoutPanel.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.layoutPanel.Size = new System.Drawing.Size(800, 600);
            this.layoutPanel.TabIndex = 0;
            //
            // labelTitle
            //
            this.labelTitle.AutoSize = true;
            this.labelTitle.Font = new System.Drawing.Font("Segoe UI", 20F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.labelTitle.Location = new System.Drawing.Point(43, 40);
            this.labelTitle.Margin = new System.Windows.Forms.Padding(3, 0, 3, 16);
            this.labelTitle.Name = "labelTitle";
            this.labelTitle.Size = new System.Drawing.Size(200, 37);
            this.labelTitle.TabIndex = 0;
            this.labelTitle.Text = "XCP-ng Center";
            //
            // labelBlurb
            //
            this.labelBlurb.AutoSize = true;
            this.labelBlurb.MaximumSize = new System.Drawing.Size(560, 0);
            this.labelBlurb.Name = "labelBlurb";
            this.labelBlurb.Margin = new System.Windows.Forms.Padding(3, 0, 3, 24);
            this.labelBlurb.Size = new System.Drawing.Size(300, 15);
            this.labelBlurb.TabIndex = 1;
            this.labelBlurb.Text = "Connect to a host or pool to get started.";
            //
            // buttonAddServer
            //
            this.buttonAddServer.AutoSize = true;
            this.buttonAddServer.Name = "buttonAddServer";
            this.buttonAddServer.Size = new System.Drawing.Size(140, 30);
            this.buttonAddServer.TabIndex = 2;
            this.buttonAddServer.Text = "Add New Server";
            this.buttonAddServer.UseVisualStyleBackColor = true;
            this.buttonAddServer.Click += new System.EventHandler(this.buttonAddServer_Click);
            //
            // HomePage
            //
            this.BackColor = System.Drawing.Color.White;
            this.Controls.Add(this.layoutPanel);
            this.Name = "HomePage";
            this.Size = new System.Drawing.Size(800, 600);
            this.layoutPanel.ResumeLayout(false);
            this.layoutPanel.PerformLayout();
            this.ResumeLayout(false);
        }

        private System.Windows.Forms.TableLayoutPanel layoutPanel;
        private System.Windows.Forms.Label labelTitle;
        private System.Windows.Forms.Label labelBlurb;
        private System.Windows.Forms.Button buttonAddServer;
    }
}
