using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace PatientCommunicationSystem
{
    public partial class MainForm : Form
    {
        // ============================================================================
        // Constants (Following Python Structure)
        // ============================================================================
        
        private const byte SYNC_BYTE = 0xAA;
        private const byte EXCODE_BYTE = 0x55;
        private const int BAUDRATE = 57600;
        
        // Blink detection parameters (will auto-calibrate)
        private double BLINK_THRESHOLD = 150;     // Start with this, will adjust
        private double BLINK_COOLDOWN = 0.3;       // Seconds between blinks
        private double BLINK_RISE_THRESHOLD = 150; // Minimum rise from baseline
        
        private const bool DEBUG = true;

        // ============================================================================
        // BlinkDetectorParser Class (Following Python Structure)
        // ============================================================================
        
        private class BlinkDetectorParser
        {
            private List<byte> buffer = new List<byte>();
            private bool inPacket = false;
            private int packetLength = 0;
            public int packetCount = 0;
            
            // EEG Data
            public List<int> rawSamples = new List<int>();
            public int poorSignal = 255;
            public int attention = 0;
            public int meditation = 0;
            
            // Blink detection variables
            public int blinkStrength = 0;
            private DateTime lastBlinkTime = DateTime.MinValue;
            public int blinkCount = 0;
            private Queue<BlinkEvent> recentBlinks = new Queue<BlinkEvent>();
            
            // For baseline calibration
            private List<double> baselineValues = new List<double>();
            public bool isCalibrated = false;
            public double blinkThreshold;
            public double riseThreshold;
            
            // Signal smoothing
            private int lastRawValue = 0;
            private Queue<int> rawHistory = new Queue<int>();
            
            // Event for blink detection
            public event Action<BlinkEvent> OnBlinkDetected;
            
            private class BlinkEvent
            {
                public DateTime Time { get; set; }
                public double Strength { get; set; }
                public double Rise { get; set; }
                public string Type { get; set; }
            }
            
            public BlinkDetectorParser()
            {
                blinkThreshold = BLINK_THRESHOLD;
                riseThreshold = BLINK_RISE_THRESHOLD;
            }
            
            public void FeedByte(byte byteVal)
            {
                buffer.Add(byteVal);
                
                // Look for sync pattern
                if (buffer.Count >= 2 && buffer[buffer.Count - 2] == SYNC_BYTE && buffer[buffer.Count - 1] == SYNC_BYTE)
                {
                    inPacket = true;
                    packetLength = 0;
                    buffer.Clear();
                    buffer.Add(SYNC_BYTE);
                    buffer.Add(SYNC_BYTE);
                    return;
                }
                
                if (!inPacket) return;
                
                // Get payload length
                if (packetLength == 0 && buffer.Count >= 3)
                {
                    packetLength = buffer[2];
                }
                
                // Check if we have complete packet (SYNC, SYNC, LENGTH, PAYLOAD, CHECKSUM)
                if (packetLength > 0 && buffer.Count >= packetLength + 4)
                {
                    ParsePacket();
                }
            }
            
            private void ParsePacket()
            {
                if (buffer.Count < 4)
                {
                    inPacket = false;
                    buffer.Clear();
                    return;
                }
                
                // Extract payload
                List<byte> payload = buffer.GetRange(3, packetLength);
                byte checksum = buffer[packetLength + 3];
                byte calcChecksum = (byte)(~payload.Sum(b => (int)b) & 0xFF);
                
                if (calcChecksum != checksum)
                {
                    inPacket = false;
                    buffer.Clear();
                    return;
                }
                
                packetCount++;
                
                // Parse payload rows
                int i = 0;
                while (i < payload.Count)
                {
                    byte code = payload[i];
                    i++;
                    
                    if (code == EXCODE_BYTE && i < payload.Count)
                    {
                        code = payload[i];
                        i++;
                    }
                    
                    if (i < payload.Count)
                    {
                        int valueLength = payload[i];
                        i++;
                        
                        if (i + valueLength <= payload.Count)
                        {
                            // Process different data types
                            switch (code)
                            {
                                case 0x02: // Poor Signal
                                    if (valueLength >= 1)
                                        poorSignal = payload[i];
                                    break;
                                    
                                case 0x04: // Attention
                                    if (valueLength == 1)
                                        attention = payload[i];
                                    else if (valueLength == 2)
                                        attention = (payload[i] << 8) | payload[i + 1];
                                    break;
                                    
                                case 0x05: // Meditation
                                    if (valueLength == 1)
                                        meditation = payload[i];
                                    else if (valueLength == 2)
                                        meditation = (payload[i] << 8) | payload[i + 1];
                                    break;
                                    
                                case 0x16: // Blink Strength (from headset)
                                    if (valueLength >= 1)
                                    {
                                        blinkStrength = payload[i];
                                        CheckForBlink();
                                    }
                                    break;
                                    
                                case 0x80: // Raw Wave (our own blink detection)
                                    if (valueLength == 2)
                                    {
                                        int raw = (payload[i] << 8) | payload[i + 1];
                                        if (raw > 32767)
                                            raw = raw - 65536;
                                        rawSamples.Add(raw);
                                        DetectBlinkFromRaw(raw);
                                    }
                                    break;
                            }
                            
                            i += valueLength;
                        }
                    }
                }
                
                inPacket = false;
                buffer.Clear();
            }
            
            private void CheckForBlink()
            {
                DateTime currentTime = DateTime.Now;
                
                // Headset sends non-zero values when a blink is detected
                if (blinkStrength > 0)
                {
                    if ((currentTime - lastBlinkTime).TotalSeconds >= BLINK_COOLDOWN)
                    {
                        blinkCount++;
                        lastBlinkTime = currentTime;
                        
                        var blinkEvent = new BlinkEvent
                        {
                            Time = currentTime,
                            Strength = blinkStrength,
                            Type = "headset"
                        };
                        
                        recentBlinks.Enqueue(blinkEvent);
                        if (recentBlinks.Count > 10)
                            recentBlinks.Dequeue();
                        
                        OnBlinkDetected?.Invoke(blinkEvent);
                    }
                }
            }
            
            private void DetectBlinkFromRaw(int rawValue)
            {
                DateTime currentTime = DateTime.Now;
                
                // Store raw value
                rawHistory.Enqueue(rawValue);
                if (rawHistory.Count > 10)
                    rawHistory.Dequeue();
                
                // Calibration phase - collect baseline
                if (!isCalibrated)
                {
                    baselineValues.Add(Math.Abs(rawValue));
                    
                    if (baselineValues.Count > 500)
                    {
                        // Calculate baseline average
                        double avgBaseline = baselineValues.Average();
                        blinkThreshold = avgBaseline * 5;  // 5x baseline
                        riseThreshold = avgBaseline * 2;
                        isCalibrated = true;
                    }
                    return;
                }
                
                // Detect blink - sudden large spike
                if (rawHistory.Count >= 3)
                {
                    double absValue = Math.Abs(rawValue);
                    double prevAbs = 0;
                    
                    if (rawHistory.Count >= 2)
                    {
                        var historyArray = rawHistory.ToArray();
                        prevAbs = Math.Abs(historyArray[historyArray.Length - 2]);
                    }
                    
                    double rise = absValue - prevAbs;
                    
                    // Check for blink signature: sharp rise then fall
                    if ((absValue > blinkThreshold || (rise > riseThreshold && absValue > 100)))
                    {
                        if ((currentTime - lastBlinkTime).TotalSeconds >= BLINK_COOLDOWN)
                        {
                            blinkCount++;
                            lastBlinkTime = currentTime;
                            
                            var blinkEvent = new BlinkEvent
                            {
                                Time = currentTime,
                                Strength = absValue,
                                Rise = rise,
                                Type = "raw"
                            };
                            
                            recentBlinks.Enqueue(blinkEvent);
                            if (recentBlinks.Count > 10)
                                recentBlinks.Dequeue();
                            
                            OnBlinkDetected?.Invoke(blinkEvent);
                        }
                    }
                }
            }
            
            public bool IsBlinkDetected()
            {
                if (recentBlinks.Count > 0)
                {
                    var lastBlink = recentBlinks.Last();
                    return (DateTime.Now - lastBlink.Time).TotalSeconds < 0.1;
                }
                return false;
            }
            
            public double GetLastBlinkStrength()
            {
                if (recentBlinks.Count > 0)
                    return recentBlinks.Last().Strength;
                return 0;
            }
        }

        // ============================================================================
        // Main Form Variables
        // ============================================================================
        
        // Serial port
        private SerialPort serialPort;
        private BlinkDetectorParser parser;
        
        // Threading
        private Thread readThread;
        private bool isRunning = false;
        
        // Patient info
        private string patientName = "PATIENT";
        private string roomNumber = "ROOM";

        // Categories
        private enum Category { URGENT_MEDICAL, PAIN, POSITION, RESPIRATORY, BATHROOM, EMOTIONAL, FAMILY, STAFF }
        private Category currentCategory = Category.URGENT_MEDICAL;
        private int currentWordIndex = 0;
        private string selectedMessage = "";
        private List<string> messageHistory = new List<string>();
        private Dictionary<Category, List<string>> wordCategories;

        // Blink detection for navigation
        private DateTime lastBlinkDetectionTime = DateTime.MinValue;
        private int blinkSequenceCount = 0;
        private DateTime sequenceStartTime = DateTime.MinValue;
        private TimeSpan blinkCooldown = TimeSpan.FromMilliseconds(750);
        private int totalBlinks = 0;
        private double lastBlinkStrength = 0;
        
        private enum Mode { Category, Word, Confirm }
        private Mode currentMode = Mode.Category;
        private bool emergencyMode = false;
        private bool audioFeedback = true;
        private bool isConnected = false;

        // CSV logging
        private StreamWriter csvWriter;
        private string csvFilename;

        // UI Controls
        private System.Windows.Forms.Timer uiTimer;
        private StatusStrip statusStrip;
        private ToolStripStatusLabel connectionStatus;
        private ToolStripStatusLabel signalStatus;
        private ToolStripStatusLabel attentionStatus;
        private ToolStripStatusLabel blinkStatus;
        private Button connectButton;
        private Button disconnectButton;
        private Button resetButton;
        private CheckBox audioCheckBox;
        private TrackBar sensitivityTrackBar;
        private ListBox categoryListBox;
        private ListBox wordListBox;
        private ListBox historyListBox;
        private Label modeLabel;
        private Label messageDisplayLabel;

        public MainForm()
        {
            this.Text = "Patient Communication System - Blink Controlled";
            this.Size = new Size(1100, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.Black;
            this.ForeColor = Color.White;

            InitializeComponent();
            InitializeWordCategories();
            LoadSettings();
            InitializeParser();
        }

        private void InitializeParser()
        {
            parser = new BlinkDetectorParser();
            parser.OnBlinkDetected += Parser_OnBlinkDetected;
        }

        private void Parser_OnBlinkDetected(BlinkDetectorParser.BlinkEvent blinkEvent)
        {
            // This is called from background thread, need to invoke UI thread
            if (this.InvokeRequired)
            {
                this.Invoke(new Action<BlinkDetectorParser.BlinkEvent>(Parser_OnBlinkDetected), blinkEvent);
                return;
            }

            totalBlinks = parser.blinkCount;
            lastBlinkStrength = blinkEvent.Strength;
            
            // Process blink for navigation
            ProcessNavigationBlink();
        }

        private void ProcessNavigationBlink()
        {
            DateTime now = DateTime.Now;

            // Apply cooldown
            if ((now - lastBlinkDetectionTime) < blinkCooldown) return;

            // Reset sequence if too long since last blink
            if ((now - sequenceStartTime) > TimeSpan.FromMilliseconds(800))
                blinkSequenceCount = 0;

            if (blinkSequenceCount == 0)
                sequenceStartTime = now;

            blinkSequenceCount++;
            lastBlinkDetectionTime = now;

            // Process based on blink count
            switch (blinkSequenceCount)
            {
                case 1:
                    Navigate();
                    if (audioFeedback) Console.Beep(1000, 50);
                    break;
                case 2:
                    if ((now - sequenceStartTime) < TimeSpan.FromMilliseconds(600))
                    {
                        SelectItem();
                        if (audioFeedback) Console.Beep(1500, 100);
                        blinkSequenceCount = 0;
                    }
                    break;
                case 3:
                    if ((now - sequenceStartTime) < TimeSpan.FromMilliseconds(900))
                    {
                        Emergency();
                        if (audioFeedback) Console.Beep(3000, 200);
                        blinkSequenceCount = 0;
                    }
                    break;
                case 4:
                    if ((now - sequenceStartTime) < TimeSpan.FromMilliseconds(1200))
                    {
                        ResetSystem();
                        if (audioFeedback)
                        {
                            Console.Beep(800, 300);
                            Console.Beep(600, 300);
                        }
                        blinkSequenceCount = 0;
                    }
                    break;
            }

            // Auto-reset sequence after timeout
            if ((DateTime.Now - sequenceStartTime) >= TimeSpan.FromMilliseconds(1000))
                blinkSequenceCount = 0;
        }

        private void InitializeComponent()
        {
            // Menu Strip
            MenuStrip menuStrip = new MenuStrip();
            menuStrip.BackColor = Color.FromArgb(30, 30, 30);

            ToolStripMenuItem fileMenu = new ToolStripMenuItem("File");
            ToolStripMenuItem connectItem = new ToolStripMenuItem("Connect", null, (s, e) => Connect());
            ToolStripMenuItem disconnectItem = new ToolStripMenuItem("Disconnect", null, (s, e) => Disconnect());
            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit", null, (s, e) => this.Close());

            ToolStripMenuItem settingsMenu = new ToolStripMenuItem("Settings");
            ToolStripMenuItem comPortItem = new ToolStripMenuItem("COM Port Settings", null, (s, e) => ShowComPortDialog());
            ToolStripMenuItem patientInfoItem = new ToolStripMenuItem("Patient Information", null, (s, e) => ShowPatientInfoDialog());

            ToolStripMenuItem helpMenu = new ToolStripMenuItem("Help");
            ToolStripMenuItem instructionsItem = new ToolStripMenuItem("Instructions", null, (s, e) => ShowInstructions());

            fileMenu.DropDownItems.Add(connectItem);
            fileMenu.DropDownItems.Add(disconnectItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(exitItem);

            settingsMenu.DropDownItems.Add(comPortItem);
            settingsMenu.DropDownItems.Add(patientInfoItem);

            helpMenu.DropDownItems.Add(instructionsItem);

            menuStrip.Items.Add(fileMenu);
            menuStrip.Items.Add(settingsMenu);
            menuStrip.Items.Add(helpMenu);

            // Status Strip
            statusStrip = new StatusStrip();
            statusStrip.BackColor = Color.FromArgb(30, 30, 30);

            connectionStatus = new ToolStripStatusLabel("● Disconnected");
            connectionStatus.ForeColor = Color.Red;
            signalStatus = new ToolStripStatusLabel("Signal: ---");
            attentionStatus = new ToolStripStatusLabel("Attention: ---");
            blinkStatus = new ToolStripStatusLabel("Blink: ---");

            statusStrip.Items.Add(connectionStatus);
            statusStrip.Items.Add(new ToolStripStatusLabel(" | "));
            statusStrip.Items.Add(signalStatus);
            statusStrip.Items.Add(new ToolStripStatusLabel(" | "));
            statusStrip.Items.Add(attentionStatus);
            statusStrip.Items.Add(new ToolStripStatusLabel(" | "));
            statusStrip.Items.Add(blinkStatus);

            // Top Control Panel
            Panel controlPanel = new Panel();
            controlPanel.Location = new Point(10, 30);
            controlPanel.Size = new Size(1060, 100);
            controlPanel.BackColor = Color.FromArgb(20, 20, 20);

            connectButton = new Button();
            connectButton.Text = "CONNECT";
            connectButton.BackColor = Color.Green;
            connectButton.ForeColor = Color.White;
            connectButton.FlatStyle = FlatStyle.Flat;
            connectButton.Location = new Point(10, 15);
            connectButton.Size = new Size(120, 35);
            connectButton.Click += (s, e) => Connect();

            disconnectButton = new Button();
            disconnectButton.Text = "DISCONNECT";
            disconnectButton.BackColor = Color.Red;
            disconnectButton.ForeColor = Color.White;
            disconnectButton.FlatStyle = FlatStyle.Flat;
            disconnectButton.Location = new Point(140, 15);
            disconnectButton.Size = new Size(120, 35);
            disconnectButton.Enabled = false;
            disconnectButton.Click += (s, e) => Disconnect();

            resetButton = new Button();
            resetButton.Text = "RESET";
            resetButton.BackColor = Color.FromArgb(60, 60, 60);
            resetButton.ForeColor = Color.White;
            resetButton.FlatStyle = FlatStyle.Flat;
            resetButton.Location = new Point(270, 15);
            resetButton.Size = new Size(120, 35);
            resetButton.Click += (s, e) => ResetSystem();

            audioCheckBox = new CheckBox();
            audioCheckBox.Text = "Audio Feedback";
            audioCheckBox.Checked = true;
            audioCheckBox.ForeColor = Color.White;
            audioCheckBox.Location = new Point(420, 20);
            audioCheckBox.Size = new Size(130, 25);
            audioCheckBox.CheckedChanged += (s, e) => audioFeedback = audioCheckBox.Checked;

            Label sensitivityLabel = new Label();
            sensitivityLabel.Text = "Sensitivity:";
            sensitivityLabel.ForeColor = Color.White;
            sensitivityLabel.Location = new Point(420, 50);
            sensitivityLabel.Size = new Size(70, 25);

            sensitivityTrackBar = new TrackBar();
            sensitivityTrackBar.Minimum = 20;
            sensitivityTrackBar.Maximum = 80;
            sensitivityTrackBar.Value = 40;
            sensitivityTrackBar.Location = new Point(500, 45);
            sensitivityTrackBar.Size = new Size(150, 45);

            Label sensitivityValue = new Label();
            sensitivityValue.Text = "40";
            sensitivityValue.ForeColor = Color.White;
            sensitivityValue.Location = new Point(660, 50);
            sensitivityValue.Size = new Size(40, 25);
            sensitivityTrackBar.Scroll += (s, e) => sensitivityValue.Text = sensitivityTrackBar.Value.ToString();

            Label instructionsLabel = new Label();
            instructionsLabel.Text = "Controls: 1 blink=Next | 2 blinks=Select | 3 blinks=Emergency | 4 blinks=Reset";
            instructionsLabel.ForeColor = Color.Yellow;
            instructionsLabel.Font = new Font("Segoe UI", 9F);
            instructionsLabel.Location = new Point(580, 15);
            instructionsLabel.Size = new Size(450, 25);

            controlPanel.Controls.Add(connectButton);
            controlPanel.Controls.Add(disconnectButton);
            controlPanel.Controls.Add(resetButton);
            controlPanel.Controls.Add(audioCheckBox);
            controlPanel.Controls.Add(sensitivityLabel);
            controlPanel.Controls.Add(sensitivityTrackBar);
            controlPanel.Controls.Add(sensitivityValue);
            controlPanel.Controls.Add(instructionsLabel);

            // Display Panel
            Panel displayPanel = new Panel();
            displayPanel.Location = new Point(10, 140);
            displayPanel.Size = new Size(700, 450);
            displayPanel.BackColor = Color.FromArgb(15, 15, 15);
            displayPanel.BorderStyle = BorderStyle.FixedSingle;

            modeLabel = new Label();
            modeLabel.Text = "Mode: SELECT CATEGORY";
            modeLabel.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            modeLabel.ForeColor = Color.Yellow;
            modeLabel.Location = new Point(10, 10);
            modeLabel.Size = new Size(500, 35);

            categoryListBox = new ListBox();
            categoryListBox.BackColor = Color.FromArgb(25, 25, 25);
            categoryListBox.ForeColor = Color.White;
            categoryListBox.Font = new Font("Segoe UI", 14F);
            categoryListBox.Location = new Point(10, 50);
            categoryListBox.Size = new Size(680, 280);

            wordListBox = new ListBox();
            wordListBox.BackColor = Color.FromArgb(25, 25, 25);
            wordListBox.ForeColor = Color.White;
            wordListBox.Font = new Font("Segoe UI", 12F);
            wordListBox.Location = new Point(10, 50);
            wordListBox.Size = new Size(680, 280);
            wordListBox.Visible = false;

            messageDisplayLabel = new Label();
            messageDisplayLabel.Font = new Font("Segoe UI", 12F);
            messageDisplayLabel.ForeColor = Color.White;
            messageDisplayLabel.Location = new Point(10, 340);
            messageDisplayLabel.Size = new Size(680, 80);
            messageDisplayLabel.Text = "No message selected";
            messageDisplayLabel.BackColor = Color.FromArgb(30, 30, 50);
            messageDisplayLabel.TextAlign = ContentAlignment.MiddleCenter;

            displayPanel.Controls.Add(modeLabel);
            displayPanel.Controls.Add(categoryListBox);
            displayPanel.Controls.Add(wordListBox);
            displayPanel.Controls.Add(messageDisplayLabel);

            // History Panel
            Panel historyPanel = new Panel();
            historyPanel.Location = new Point(720, 140);
            historyPanel.Size = new Size(350, 450);
            historyPanel.BackColor = Color.FromArgb(20, 20, 20);
            historyPanel.BorderStyle = BorderStyle.FixedSingle;

            Label historyTitle = new Label();
            historyTitle.Text = "MESSAGE HISTORY";
            historyTitle.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            historyTitle.ForeColor = Color.Cyan;
            historyTitle.Location = new Point(10, 10);
            historyTitle.Size = new Size(200, 30);

            historyListBox = new ListBox();
            historyListBox.BackColor = Color.FromArgb(25, 25, 25);
            historyListBox.ForeColor = Color.White;
            historyListBox.Font = new Font("Segoe UI", 9F);
            historyListBox.Location = new Point(10, 45);
            historyListBox.Size = new Size(330, 360);

            Button clearButton = new Button();
            clearButton.Text = "Clear History";
            clearButton.BackColor = Color.FromArgb(60, 60, 60);
            clearButton.ForeColor = Color.White;
            clearButton.FlatStyle = FlatStyle.Flat;
            clearButton.Location = new Point(10, 410);
            clearButton.Size = new Size(100, 30);
            clearButton.Click += (s, e) => { messageHistory.Clear(); historyListBox.Items.Clear(); };

            Button exportButton = new Button();
            exportButton.Text = "Export Logs";
            exportButton.BackColor = Color.FromArgb(60, 60, 60);
            exportButton.ForeColor = Color.White;
            exportButton.FlatStyle = FlatStyle.Flat;
            exportButton.Location = new Point(120, 410);
            exportButton.Size = new Size(100, 30);
            exportButton.Click += (s, e) => ExportLogs();

            historyPanel.Controls.Add(historyTitle);
            historyPanel.Controls.Add(historyListBox);
            historyPanel.Controls.Add(clearButton);
            historyPanel.Controls.Add(exportButton);

            // Add all to form
            this.Controls.Add(menuStrip);
            this.Controls.Add(controlPanel);
            this.Controls.Add(displayPanel);
            this.Controls.Add(historyPanel);
            this.Controls.Add(statusStrip);
            this.MainMenuStrip = menuStrip;

            // Timer for UI updates
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 100;
            uiTimer.Tick += (s, e) => UpdateUI();
            uiTimer.Start();
        }

        private void InitializeWordCategories()
        {
            wordCategories = new Dictionary<Category, List<string>>
            {
                [Category.URGENT_MEDICAL] = new List<string>
                {
                    "🚨 EMERGENCY - NEED NURSE NOW!",
                    "⚠️ I'M IN DISTRESS",
                    "💊 MEDICATION REACTION",
                    "🩸 I'M BLEEDING",
                    "🤢 VOMITING",
                    "🔥 FEVER",
                    "💔 CHEST PAIN"
                },
                [Category.PAIN] = new List<string>
                {
                    "🔴 SEVERE PAIN",
                    "🟠 MODERATE PAIN",
                    "🟡 MILD PAIN",
                    "🧠 HEADACHE",
                    "🫀 STOMACH PAIN",
                    "🦴 BACK PAIN"
                },
                [Category.POSITION] = new List<string>
                {
                    "🔄 NEED TO BE TURNED",
                    "🪑 NEED TO SIT UP",
                    "📏 NEED TO LIE DOWN",
                    "🛏️ CHANGE POSITION"
                },
                [Category.RESPIRATORY] = new List<string>
                {
                    "😤 CAN'T BREATHE!",
                    "😮‍💨 SHORTNESS OF BREATH",
                    "🫁 NEED OXYGEN",
                    "🌬️ NEED SUCTIONING"
                },
                [Category.BATHROOM] = new List<string>
                {
                    "🚽 NEED BEDPAN",
                    "💧 NEED URINAL",
                    "💩 NEED BOWEL MOVEMENT",
                    "🧻 NEED DIAPER CHANGE"
                },
                [Category.EMOTIONAL] = new List<string>
                {
                    "😭 I'M SCARED",
                    "😰 ANXIOUS",
                    "😞 DEPRESSED",
                    "😤 FRUSTRATED"
                },
                [Category.FAMILY] = new List<string>
                {
                    "📞 CALL MY FAMILY",
                    "📱 NEED TO SEE FAMILY",
                    "👪 FAMILY VISIT REQUEST"
                },
                [Category.STAFF] = new List<string>
                {
                    "👩‍⚕️ NEED TO SPEAK WITH NURSE",
                    "💊 NEED MEDICATION",
                    "🍽️ NEED TO EAT/DRINK",
                    "🥤 NEED WATER"
                }
            };
        }

        // ============================================================================
        // Serial Port Connection (Following Python Structure)
        // ============================================================================

        private string FindHeadsetPort()
        {
            string[] ports = SerialPort.GetPortNames();
            
            foreach (string portName in ports)
            {
                try
                {
                    using (SerialPort testPort = new SerialPort(portName, BAUDRATE))
                    {
                        testPort.ReadTimeout = 500;
                        testPort.Open();
                        Thread.Sleep(500);
                        
                        if (testPort.BytesToRead > 0)
                        {
                            return portName;
                        }
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }
            return null;
        }

        private void Connect()
        {
            string comPort = "COM4";  // Default, can be changed via settings
            
            // Try to find headset if default port fails
            if (!System.IO.Ports.SerialPort.GetPortNames().Contains(comPort))
            {
                comPort = FindHeadsetPort();
            }
            
            if (string.IsNullOrEmpty(comPort))
            {
                MessageBox.Show("No NeuroSky headset found! Please check:\n" +
                    "1. Headset is ON\n" +
                    "2. USB dongle is plugged in\n" +
                    "3. Try unplugging/replugging dongle",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                serialPort = new SerialPort(comPort, BAUDRATE);
                serialPort.ReadTimeout = 10;
                serialPort.Open();
                
                // Initialize CSV logging
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                csvFilename = $"blink_data_{timestamp}.csv";
                csvWriter = new StreamWriter(csvFilename);
                csvWriter.WriteLine("timestamp,packet, poor_signal, attention, meditation, header_blink, raw_blink, blink_strength, raw_value, total_blinks");
                csvWriter.Flush();
                
                isRunning = true;
                readThread = new Thread(ReadSerialData);
                readThread.Start();
                
                isConnected = true;
                connectionStatus.Text = "● Connected";
                connectionStatus.ForeColor = Color.Green;
                connectButton.Enabled = false;
                disconnectButton.Enabled = true;
                
                AddToHistory($"Connected to {comPort}");
                AddToHistory($"Logging to: {csvFilename}");
                AddToHistory("Calibrating... Please blink normally");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to connect: {ex.Message}",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Disconnect()
        {
            isRunning = false;
            
            if (readThread != null && readThread.IsAlive)
                readThread.Join(1000);
            
            if (serialPort != null && serialPort.IsOpen)
                serialPort.Close();
            
            if (csvWriter != null)
            {
                csvWriter.Close();
                csvWriter = null;
            }
            
            isConnected = false;
            connectionStatus.Text = "● Disconnected";
            connectionStatus.ForeColor = Color.Red;
            connectButton.Enabled = true;
            disconnectButton.Enabled = false;
            
            AddToHistory("Disconnected");
        }

        private void ReadSerialData()
        {
            while (isRunning)
            {
                try
                {
                    if (serialPort != null && serialPort.IsOpen && serialPort.BytesToRead > 0)
                    {
                        byte[] data = new byte[serialPort.BytesToRead];
                        serialPort.Read(data, 0, data.Length);
                        
                        foreach (byte b in data)
                        {
                            parser.FeedByte(b);
                        }
                        
                        // Log raw samples to CSV
                        if (parser.rawSamples.Count > 0)
                        {
                            foreach (int rawVal in parser.rawSamples)
                            {
                                if (csvWriter != null)
                                {
                                    lock (csvWriter)
                                    {
                                        csvWriter.WriteLine($"{DateTime.Now:O},{parser.packetCount}," +
                                            $"{parser.poorSignal},{parser.attention},{parser.meditation}," +
                                            $"{(parser.blinkStrength > 0 ? 1 : 0)},{(parser.IsBlinkDetected() ? 1 : 0)}," +
                                            $"{parser.GetLastBlinkStrength()},{rawVal},{parser.blinkCount}");
                                        csvWriter.Flush();
                                    }
                                }
                            }
                            parser.rawSamples.Clear();
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Handle timeout and other exceptions
                    if (isRunning)
                    {
                        Console.WriteLine($"Serial read error: {ex.Message}");
                    }
                }
                
                Thread.Sleep(1);
            }
        }

        // Navigation Methods
        private void Navigate()
        {
            if (emergencyMode) return;

            if (currentMode == Mode.Category)
            {
                var categories = Enum.GetValues(typeof(Category));
                int nextIndex = ((int)currentCategory + 1) % categories.Length;
                currentCategory = (Category)nextIndex;
                currentWordIndex = 0;
            }
            else if (currentMode == Mode.Word)
            {
                var words = wordCategories[currentCategory];
                currentWordIndex = (currentWordIndex + 1) % words.Count;
            }
        }

        private void SelectItem()
        {
            if (emergencyMode) return;

            if (currentMode == Mode.Category)
            {
                currentMode = Mode.Word;
                currentWordIndex = 0;
                RefreshWordList();
            }
            else if (currentMode == Mode.Word)
            {
                selectedMessage = wordCategories[currentCategory][currentWordIndex];
                currentMode = Mode.Confirm;
                messageDisplayLabel.Text = $"CONFIRM: {selectedMessage}";
                messageDisplayLabel.BackColor = Color.FromArgb(50, 50, 80);
            }
            else if (currentMode == Mode.Confirm)
            {
                SendMessage();
            }
        }

        private void RefreshWordList()
        {
            wordListBox.Items.Clear();
            foreach (string word in wordCategories[currentCategory])
            {
                wordListBox.Items.Add(word);
            }
            if (currentWordIndex < wordListBox.Items.Count)
                wordListBox.SelectedIndex = currentWordIndex;
        }

        private void SendMessage()
        {
            string fullMessage = $"[{DateTime.Now:HH:mm:ss}] {patientName} (Rm {roomNumber}): {selectedMessage}";
            AddToHistory(fullMessage);

            if (currentCategory == Category.URGENT_MEDICAL)
            {
                MessageBox.Show(fullMessage, "URGENT MESSAGE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (audioFeedback)
            {
                Console.Beep(2000, 200);
                Thread.Sleep(100);
                Console.Beep(2500, 200);
            }

            messageDisplayLabel.Text = $"✓ SENT: {selectedMessage}";
            messageDisplayLabel.BackColor = Color.DarkGreen;

            currentMode = Mode.Category;
            currentWordIndex = 0;
            selectedMessage = "";

            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 3000;
            timer.Tick += (s, e) =>
            {
                messageDisplayLabel.Text = "No message selected";
                messageDisplayLabel.BackColor = Color.FromArgb(30, 30, 50);
                timer.Stop();
            };
            timer.Start();
        }

        private void Emergency()
        {
            emergencyMode = true;
            string emergencyMsg = $"🚨 EMERGENCY - {patientName} - Room {roomNumber} - IMMEDIATE ASSISTANCE 🚨";
            AddToHistory(emergencyMsg);

            for (int i = 0; i < 5; i++)
            {
                if (audioFeedback) Console.Beep(3000, 200);
                Thread.Sleep(100);
            }

            MessageBox.Show(emergencyMsg, "EMERGENCY ALERT", MessageBoxButtons.OK, MessageBoxIcon.Error);

            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 10000;
            timer.Tick += (s, e) =>
            {
                emergencyMode = false;
                timer.Stop();
            };
            timer.Start();
        }

        private void ResetSystem()
        {
            currentMode = Mode.Category;
            currentWordIndex = 0;
            selectedMessage = "";
            blinkSequenceCount = 0;
            emergencyMode = false;
            messageDisplayLabel.Text = "No message selected";
            messageDisplayLabel.BackColor = Color.FromArgb(30, 30, 50);

            AddToHistory("System reset");
        }

        private void UpdateUI()
        {
            if (!isConnected) return;

            string signalText = parser.poorSignal == 0 ? "Good" : parser.poorSignal < 50 ? "Fair" : "Poor";
            signalStatus.Text = $"Signal: {signalText}";
            signalStatus.ForeColor = parser.poorSignal == 0 ? Color.Green : parser.poorSignal < 50 ? Color.Yellow : Color.Red;
            attentionStatus.Text = $"Attention: {parser.attention}";
            
            string calStatus = parser.isCalibrated ? "Calibrated" : "Calibrating...";
            blinkStatus.Text = $"Blinks: {totalBlinks} | {calStatus}";

            if (!emergencyMode)
            {
                if (currentMode == Mode.Category)
                {
                    modeLabel.Text = $"Mode: SELECT CATEGORY ({calStatus})";
                    categoryListBox.Visible = true;
                    wordListBox.Visible = false;

                    if (categoryListBox.Items.Count == 0)
                    {
                        foreach (Category cat in Enum.GetValues(typeof(Category)))
                            categoryListBox.Items.Add(cat.ToString());
                    }
                    categoryListBox.SelectedIndex = (int)currentCategory;
                }
                else if (currentMode == Mode.Word)
                {
                    modeLabel.Text = $"Mode: SELECT MESSAGE - {currentCategory}";
                    categoryListBox.Visible = false;
                    wordListBox.Visible = true;
                }
                else if (currentMode == Mode.Confirm)
                {
                    modeLabel.Text = "Mode: CONFIRM MESSAGE";
                }
            }
            else
            {
                modeLabel.Text = "🚨 EMERGENCY MODE ACTIVE 🚨";
                modeLabel.ForeColor = Color.Red;
            }
        }

        private void AddToHistory(string message)
        {
            messageHistory.Add(message);
            historyListBox.Items.Insert(0, message);
            if (historyListBox.Items.Count > 100)
                historyListBox.Items.RemoveAt(historyListBox.Items.Count - 1);
        }

        // Settings and Utility Methods
        private void ShowComPortDialog()
        {
            string result = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter COM Port (e.g., COM3, COM4, COM5):",
                "COM Port Settings",
                "COM4");

            if (!string.IsNullOrWhiteSpace(result))
            {
                SaveSettings();
                MessageBox.Show($"COM port set to {result}. Reconnect to apply.",
                    "Settings Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ShowPatientInfoDialog()
        {
            string name = Microsoft.VisualBasic.Interaction.InputBox("Enter Patient Name:", "Patient Information", patientName);
            if (!string.IsNullOrWhiteSpace(name))
            {
                patientName = name.ToUpper();
                string room = Microsoft.VisualBasic.Interaction.InputBox("Enter Room Number:", "Room Information", roomNumber);
                if (!string.IsNullOrWhiteSpace(room))
                {
                    roomNumber = room.ToUpper();
                    SaveSettings();
                    AddToHistory($"Patient info: {patientName}, Room {roomNumber}");
                }
            }
        }

        private void ShowInstructions()
        {
            MessageBox.Show(
                "BLINK CONTROLS:\n\n" +
                "• 1 BLINK → Next option\n" +
                "• 2 BLINKS → Select/Confirm\n" +
                "• 3 BLINKS → EMERGENCY\n" +
                "• 4 BLINKS → Reset all\n\n" +
                "WORKFLOW:\n" +
                "1. Wait for calibration (10 seconds)\n" +
                "2. Blink twice to select a category\n" +
                "3. Blink twice to select a message\n" +
                "4. Blink twice to confirm and send\n\n" +
                "DETECTION:\n" +
                "• Auto-calibrates to your blinks\n" +
                "• Works with raw EEG data\n" +
                "• Cooldown prevents double-detection",
                "Instructions", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExportLogs()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|CSV files (*.csv)|*.csv";
                dialog.FileName = $"PatientComm_{patientName}_{DateTime.Now:yyyyMMdd_HHmmss}.log";

                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllLines(dialog.FileName, messageHistory);
                    MessageBox.Show($"Logs exported to:\n{dialog.FileName}",
                        "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        private void LoadSettings()
        {
            string configFile = "PatientComm.config";
            if (File.Exists(configFile))
            {
                try
                {
                    string[] lines = File.ReadAllLines(configFile);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("PatientName="))
                            patientName = line.Substring(12);
                        else if (line.StartsWith("RoomNumber="))
                            roomNumber = line.Substring(11);
                    }
                }
                catch { }
            }
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllLines("PatientComm.config", new[]
                {
                    $"PatientName={patientName}",
                    $"RoomNumber={roomNumber}"
                });
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (isConnected)
                Disconnect();
            SaveSettings();
            base.OnFormClosing(e);
        }
    }
}