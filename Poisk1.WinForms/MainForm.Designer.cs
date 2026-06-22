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
        _status.SuspendLayout();
        SuspendLayout();
        // 
        // _menu
        // 
        _menu.ImageScalingSize = new Size(24, 24);
        _menu.Location = new Point(0, 0);
        _menu.Name = "_menu";
        _menu.Size = new Size(1000, 24);
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
        _screenPanel.Size = new Size(1000, 770);
        _screenPanel.TabIndex = 0;
        // 
        // _screen
        // 
        _screen.BackColor = Color.Black;
        _screen.Dock = DockStyle.Fill;
        _screen.Location = new Point(10, 20);
        _screen.Name = "_screen";
        _screen.Size = new Size(980, 740);
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
        _log.Location = new Point(0, 794);
        _log.Multiline = true;
        _log.Name = "_log";
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Size = new Size(1000, 160);
        _log.TabIndex = 1;
        _log.TabStop = false;
        // 
        // _status
        // 
        _status.BackColor = Color.FromArgb(30, 30, 30);
        _status.ForeColor = Color.Gainsboro;
        _status.ImageScalingSize = new Size(24, 24);
        _status.Items.AddRange(new ToolStripItem[] { _stMachine, _stRam, _stVideo, _stCassette, _stCpu, _stSpring, _stFreq, _stState });
        _status.Location = new Point(0, 954);
        _status.Name = "_status";
        _status.Size = new Size(1000, 36);
        _status.SizingGrip = false;
        _status.TabIndex = 3;
        // 
        // _stMachine
        // 
        _stMachine.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _stMachine.Name = "_stMachine";
        _stMachine.Size = new Size(34, 29);
        _stMachine.Text = "—";
        // 
        // _stRam
        // 
        _stRam.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _stRam.Name = "_stRam";
        _stRam.Size = new Size(80, 29);
        _stRam.Text = "ОЗП: —";
        // 
        // _stVideo
        // 
        _stVideo.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _stVideo.Name = "_stVideo";
        _stVideo.Size = new Size(87, 29);
        _stVideo.Text = "Відео: —";
        // 
        // _stCassette
        // 
        _stCassette.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _stCassette.Name = "_stCassette";
        _stCassette.Size = new Size(104, 29);
        _stCassette.Text = "Стрічка: —";
        // 
        // _stCpu
        // 
        _stCpu.BorderSides = ToolStripStatusLabelBorderSides.Right;
        _stCpu.Font = new Font("Consolas", 8F);
        _stCpu.Name = "_stCpu";
        _stCpu.Size = new Size(94, 29);
        _stCpu.Text = "----:----";
        // 
        // _stSpring
        // 
        _stSpring.Name = "_stSpring";
        _stSpring.Size = new Size(516, 29);
        _stSpring.Spring = true;
        // 
        // _stFreq
        // 
        _stFreq.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _stFreq.ForeColor = Color.Khaki;
        _stFreq.Name = "_stFreq";
        _stFreq.Size = new Size(66, 29);
        _stFreq.Text = "5 МГц";
        // 
        // _stState
        // 
        _stState.BorderSides = ToolStripStatusLabelBorderSides.Left;
        _stState.Name = "_stState";
        _stState.Size = new Size(4, 29);
        // 
        // MainForm
        // 
        AutoScaleDimensions = new SizeF(10F, 25F);
        AutoScaleMode = AutoScaleMode.Font;
        BackColor = Color.Black;
        ClientSize = new Size(1000, 990);
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
        _status.ResumeLayout(false);
        _status.PerformLayout();
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
