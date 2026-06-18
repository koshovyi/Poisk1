namespace Poisk1.WinForms;

partial class MainForm
{
    /// <summary>Required designer variable.</summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>Clean up any resources being used.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && components != null)
            components.Dispose();
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        _menu = new MenuStrip();
        _screenPanel = new Panel();
        _screen = new PictureBox();
        _log = new TextBox();
        _timer = new System.Windows.Forms.Timer(components);
        _status = new StatusStrip();
        _stMachine = new ToolStripStatusLabel();
        _stRam = new ToolStripStatusLabel();
        _stVideo = new ToolStripStatusLabel();
        _stCassette = new ToolStripStatusLabel();
        _stCpu = new ToolStripStatusLabel();
        _stSpring = new ToolStripStatusLabel();
        _stFreq = new ToolStripStatusLabel();
        _stState = new ToolStripStatusLabel();
        _screenPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)_screen).BeginInit();
        SuspendLayout();
        // 
        // _menu
        // 
        _menu.ImageScalingSize = new Size(24, 24);
        _menu.Location = new Point(0, 0);
        _menu.Name = "_menu";
        _menu.Size = new Size(640, 24);
        _menu.TabIndex = 2;
        _menu.Text = "menu";
        // 
        // _screenPanel
        // 
        _screenPanel.BackColor = Color.Black;
        _screenPanel.Controls.Add(_screen);
        _screenPanel.Dock = DockStyle.Fill;
        _screenPanel.Location = new Point(0, 24);
        _screenPanel.Name = "_screenPanel";
        _screenPanel.Padding = new Padding(10, 20, 10, 10);
        _screenPanel.Size = new Size(640, 400);
        _screenPanel.TabIndex = 0;
        // 
        // _screen
        // 
        _screen.BackColor = Color.Black;
        _screen.Dock = DockStyle.Fill;
        _screen.Location = new Point(10, 20);
        _screen.Name = "_screen";
        _screen.Size = new Size(620, 370);
        _screen.SizeMode = PictureBoxSizeMode.Zoom;
        _screen.TabIndex = 0;
        _screen.TabStop = false;
        // 
        // _log
        // 
        _log.BackColor = Color.FromArgb(20, 20, 20);
        _log.Dock = DockStyle.Bottom;
        _log.Font = new Font("Courier New", 8.5F);
        _log.ForeColor = Color.LightGreen;
        _log.Location = new Point(0, 424);
        _log.Multiline = true;
        _log.Name = "_log";
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Size = new Size(640, 160);
        _log.TabIndex = 1;
        _log.TabStop = false;
        //
        // _status
        //
        _status.BackColor = Color.FromArgb(30, 30, 30);
        _status.Items.AddRange(new ToolStripItem[] { _stMachine, _stRam, _stVideo, _stCassette, _stCpu, _stSpring, _stFreq, _stState });
        _status.Location = new Point(0, 562);
        _status.Name = "_status";
        _status.Size = new Size(640, 22);
        _status.SizingGrip = false;
        _status.TabIndex = 3;
        _status.ForeColor = Color.Gainsboro;
        _stMachine.Name = "_stMachine"; _stMachine.BorderSides = ToolStripStatusLabelBorderSides.Right; _stMachine.Text = "—";
        _stRam.Name = "_stRam"; _stRam.BorderSides = ToolStripStatusLabelBorderSides.Right; _stRam.Text = "ОЗП: —";
        _stVideo.Name = "_stVideo"; _stVideo.BorderSides = ToolStripStatusLabelBorderSides.Right; _stVideo.Text = "Відео: —";
        _stCassette.Name = "_stCassette"; _stCassette.BorderSides = ToolStripStatusLabelBorderSides.Right; _stCassette.Text = "Стрічка: —";
        _stCpu.Name = "_stCpu"; _stCpu.BorderSides = ToolStripStatusLabelBorderSides.Right; _stCpu.Font = new Font("Consolas", 8F); _stCpu.Text = "----:----";
        _stSpring.Name = "_stSpring"; _stSpring.Spring = true;
        _stFreq.Name = "_stFreq"; _stFreq.BorderSides = ToolStripStatusLabelBorderSides.Left; _stFreq.ForeColor = Color.Khaki; _stFreq.Text = "5 МГц";
        _stState.Name = "_stState"; _stState.BorderSides = ToolStripStatusLabelBorderSides.Left; _stState.Text = "";
        //
        // MainForm
        //
        AutoScaleDimensions = new SizeF(10F, 25F);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.Black;
        ClientSize = new Size(640, 584);
        Controls.Add(_screenPanel);
        Controls.Add(_log);
        Controls.Add(_menu);
        Controls.Add(_status);
        KeyPreview = true;
        MainMenuStrip = _menu;
        Name = "MainForm";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Поиск-1 (емулятор)";
        _screenPanel.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)_screen).EndInit();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private MenuStrip _menu;
    private Panel _screenPanel;
    private PictureBox _screen;
    private TextBox _log;
    private System.Windows.Forms.Timer _timer;
    private StatusStrip _status;
    private ToolStripStatusLabel _stMachine;
    private ToolStripStatusLabel _stRam;
    private ToolStripStatusLabel _stVideo;
    private ToolStripStatusLabel _stCassette;
    private ToolStripStatusLabel _stCpu;
    private ToolStripStatusLabel _stSpring;
    private ToolStripStatusLabel _stFreq;
    private ToolStripStatusLabel _stState;
}
