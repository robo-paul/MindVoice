using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using libStreamSDK;

namespace PatientCommunicationSystem
{
    public partial class MainForm : Form
    {
        // NeuroSky variables
        private int connectionId;
        private bool isRunning = false;
        private Thread dataThread;
        private string comPort = "COM4";

        // NeuroSky data
        private int packetCount = 0;
        private int attentionValue = 0;
        private int blinkStrength = 0;
        private int poorSignal = 255;
        private const int TG_DATA_BLINK_STRENGTH = 22;

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

        // Blink detection
        private DateTime lastBlinkTime = DateTime.MinValue;
        private int blinkSequenceCount = 0;
        private DateTime sequenceStartTime = DateTime.MinValue;
        private TimeSpan blinkCooldown = TimeSpan.FromMilliseconds(750);
        private int blinkThreshold = 40;
        private bool blinkDetected = false;

        private enum Mode { Category, Word, Confirm }
        private Mode currentMode = Mode.Category;

        private bool emergencyMode = false;
        private bool audioFeedback = true;
        private bool isConnected = false;

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
            InitializeComponent();
            InitializeWordCategories();
            LoadSettings();
        }

        private void InitializeComponent()
        {
            // Form settings
            this.Text = "Patient Communication System - Blink Controlled";
            this.Size = new Size(1100, 700);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.Black;
            this.ForeColor = Color.White;

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
            sensitivityTrackBar.Value = blinkThreshold;
            sensitivityTrackBar.Location = new Point(500, 45);
            sensitivityTrackBar.Size = new Size(150, 45);
            sensitivityTrackBar.Scroll += (s, e) => blinkThreshold = sensitivityTrackBar.Value;

            Label sensitivityValue = new Label();
            sensitivityValue.Text = blinkThreshold.ToString();
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

            // Display Panel (Categories/Words)
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

        private void Connect()
        {
            if (ConnectToHeadset(comPort))
            {
                isConnected = true;
                connectionStatus.Text = "● Connected";
                connectionStatus.ForeColor = Color.Green;
                connectButton.Enabled = false;
                disconnectButton.Enabled = true;
                AddToHistory("System connected");
            }
            else
            {
                MessageBox.Show($"Failed to connect to {comPort}. Check headset and try again.",
                    "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Disconnect()
        {
            isRunning = false;
            if (dataThread != null && dataThread.IsAlive)
                dataThread.Join(1000);
            if (connectionId != 0)
            {
                NativeThinkgear.TG_Disconnect(connectionId);
                NativeThinkgear.TG_FreeConnection(connectionId);
            }
            isConnected = false;
            connectionStatus.Text = "● Disconnected";
            connectionStatus.ForeColor = Color.Red;
            connectButton.Enabled = true;
            disconnectButton.Enabled = false;
            AddToHistory("System disconnected");
        }

        private bool ConnectToHeadset(string port)
        {
            connectionId = NativeThinkgear.TG_GetNewConnectionId();
            if (connectionId < 0) return false;

            int errCode = NativeThinkgear.TG_Connect(connectionId, port,
                NativeThinkgear.TG_BAUD_57600, NativeThinkgear.TG_STREAM_PACKETS);

            if (errCode < 0) return false;

            isRunning = true;
            dataThread = new Thread(ReadDataThread);
            dataThread.Start();

            return true;
        }

        private void ReadDataThread()
        {
            while (isRunning)
            {
                int packetsRead = NativeThinkgear.TG_ReadPackets(connectionId, -1);

                if (packetsRead > 0)
                {
                    packetCount += packetsRead;

                    if (NativeThinkgear.TG_GetValueStatus(connectionId, NativeThinkgear.TG_DATA_POOR_SIGNAL) != 0)
                        poorSignal = (int)NativeThinkgear.TG_GetValue(connectionId, NativeThinkgear.TG_DATA_POOR_SIGNAL);

                    if (NativeThinkgear.TG_GetValueStatus(connectionId, NativeThinkgear.TG_DATA_ATTENTION) != 0)
                        attentionValue = (int)NativeThinkgear.TG_GetValue(connectionId, NativeThinkgear.TG_DATA_ATTENTION);

                    if (NativeThinkgear.TG_GetValueStatus(connectionId, TG_DATA_BLINK_STRENGTH) != 0)
                    {
                        int newBlinkStrength = (int)NativeThinkgear.TG_GetValue(connectionId, TG_DATA_BLINK_STRENGTH);

                        if (newBlinkStrength > blinkThreshold && !blinkDetected)
                        {
                            blinkDetected = true;
                            ProcessBlink();
                        }
                        else if (newBlinkStrength <= blinkThreshold && blinkDetected)
                        {
                            blinkDetected = false;
                        }

                        blinkStrength = newBlinkStrength;
                    }
                }
                Thread.Sleep(10);
            }
        }

        private void ProcessBlink()
        {
            DateTime now = DateTime.Now;

            if ((now - lastBlinkTime) < blinkCooldown) return;

            if ((now - sequenceStartTime) > TimeSpan.FromMilliseconds(800))
                blinkSequenceCount = 0;

            if (blinkSequenceCount == 0)
                sequenceStartTime = now;

            blinkSequenceCount++;
            lastBlinkTime = now;

            switch (blinkSequenceCount)
            {
                case 1:
                    this.Invoke(new Action(() => Navigate()));
                    if (audioFeedback) Console.Beep(1000, 50);
                    break;
                case 2:
                    if ((now - sequenceStartTime) < TimeSpan.FromMilliseconds(600))
                    {
                        this.Invoke(new Action(() => SelectItem()));
                        if (audioFeedback) Console.Beep(1500, 100);
                        blinkSequenceCount = 0;
                    }
                    break;
                case 3:
                    if ((now - sequenceStartTime) < TimeSpan.FromMilliseconds(900))
                    {
                        this.Invoke(new Action(() => Emergency()));
                        blinkSequenceCount = 0;
                    }
                    break;
                case 4:
                    if ((now - sequenceStartTime) < TimeSpan.FromMilliseconds(1200))
                    {
                        this.Invoke(new Action(() => ResetSystem()));
                        blinkSequenceCount = 0;
                    }
                    break;
            }

            if ((DateTime.Now - sequenceStartTime) >= TimeSpan.FromMilliseconds(1000))
                blinkSequenceCount = 0;
        }

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
            if (wordListBox.InvokeRequired)
            {
                wordListBox.Invoke(new Action(RefreshWordList));
                return;
            }

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

            // Reset after sending
            currentMode = Mode.Category;
            currentWordIndex = 0;
            selectedMessage = "";

            // Reset display after 3 seconds
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

            if (audioFeedback)
            {
                Console.Beep(800, 300);
                Console.Beep(600, 300);
            }

            AddToHistory("System reset");
        }

        private void UpdateUI()
        {
            if (!isConnected) return;

            string signalText = poorSignal == 0 ? "Good" : poorSignal < 50 ? "Fair" : "Poor";
            signalStatus.Text = $"Signal: {signalText}";
            signalStatus.ForeColor = poorSignal == 0 ? Color.Green : poorSignal < 50 ? Color.Yellow : Color.Red;
            attentionStatus.Text = $"Attention: {attentionValue}";
            blinkStatus.Text = $"Blink: {blinkStrength}";

            if (!emergencyMode)
            {
                if (currentMode == Mode.Category)
                {
                    modeLabel.Text = "Mode: SELECT CATEGORY (1 blink=next, 2 blinks=select)";
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
                    modeLabel.Text = $"Mode: SELECT MESSAGE - {currentCategory} (1 blink=next, 2 blinks=select)";
                    categoryListBox.Visible = false;
                    wordListBox.Visible = true;
                }
            }
        }

        private void AddToHistory(string message)
        {
            if (historyListBox.InvokeRequired)
            {
                historyListBox.Invoke(new Action<string>(AddToHistory), message);
                return;
            }

            messageHistory.Add(message);
            historyListBox.Items.Insert(0, message);
            if (historyListBox.Items.Count > 100)
                historyListBox.Items.RemoveAt(historyListBox.Items.Count - 1);
        }

        private void ShowComPortDialog()
        {
            string result = Microsoft.VisualBasic.Interaction.InputBox(
                "Enter COM Port (e.g., COM3, COM4, COM5):",
                "COM Port Settings",
                comPort);

            if (!string.IsNullOrWhiteSpace(result))
            {
                comPort = result.ToUpper();
                SaveSettings();
                MessageBox.Show($"COM port set to {comPort}. Reconnect to apply.",
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
                "1. Blink twice to select a category\n" +
                "2. Blink twice to select a message\n" +
                "3. Blink twice to confirm and send\n\n" +
                "All messages are logged and can be exported.",
                "Instructions", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ExportLogs()
        {
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt";
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
                        if (line.StartsWith("ComPort="))
                            comPort = line.Substring(8);
                        else if (line.StartsWith("PatientName="))
                            patientName = line.Substring(12);
                        else if (line.StartsWith("RoomNumber="))
                            roomNumber = line.Substring(11);
                        else if (line.StartsWith("BlinkThreshold="))
                            int.TryParse(line.Substring(15), out blinkThreshold);
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
                    $"ComPort={comPort}",
                    $"PatientName={patientName}",
                    $"RoomNumber={roomNumber}",
                    $"BlinkThreshold={blinkThreshold}"
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